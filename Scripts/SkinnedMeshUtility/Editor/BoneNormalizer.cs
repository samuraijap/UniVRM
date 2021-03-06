﻿using System;
using System.Collections.Generic;
using System.Linq;
using UniHumanoid;
using UnityEngine;


namespace VRM
{
    public static class BoneNormalizer
    {
        /// <summary>
        /// 回転とスケールを除去したヒエラルキーをコピーする
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dst"></param>
        static void CopyAndBuild(Transform src, Transform dst, Dictionary<Transform, Transform> boneMap)
        {
            boneMap[src] = dst;

            foreach (Transform child in src)
            {
                var dstChild = new GameObject(child.name);
                dstChild.transform.SetParent(dst);
                dstChild.transform.position = child.position; // copy position only

                //dstChild.AddComponent<UniHumanoid.BoneGizmoDrawer>();

                CopyAndBuild(child, dstChild.transform, boneMap);
            }
        }

        static IEnumerable<Transform> Traverse(this Transform t)
        {
            yield return t;
            foreach (Transform child in t)
            {
                foreach (var x in child.Traverse())
                {
                    yield return x;
                }
            }
        }

        public static GameObject Execute(GameObject go, Dictionary<Transform, Transform> boneMap, bool forceTPose)
        {
            //
            // T-Poseにする
            //
            if(forceTPose)
            {
                var animator = go.GetComponent<Animator>();
                if (animator == null)
                {
                    throw new ArgumentException("Animator with avatar is required");
                }

                var avatar = animator.avatar;
                if (avatar == null)
                {
                    throw new ArgumentException("avatar is required");
                }

                if (!avatar.isValid)
                {
                    throw new ArgumentException("invalid avatar");
                }

                if (!avatar.isHuman)
                {
                    throw new ArgumentException("avatar is not human");
                }

                HumanPoseTransfer.SetTPose(avatar, go.transform);
            }

            //
            // 回転・スケールの無いヒエラルキーをコピーする
            //
            var normalized = new GameObject(go.name + "(normalized)");
            normalized.transform.position = go.transform.position;

            CopyAndBuild(go.transform, normalized.transform, boneMap);

            //
            // 新しいヒエラルキーからAvatarを作る
            //
            {
                var src = go.GetComponent<Animator>();

                var map = Enum.GetValues(typeof(HumanBodyBones))
                    .Cast<HumanBodyBones>()
                    .Where(x => x != HumanBodyBones.LastBone)
                    .Select(x => new { Key = x, Value = src.GetBoneTransform(x) })
                    .Where(x => x.Value != null)
                    .ToDictionary(x => x.Key, x => boneMap[x.Value])
                    ;

                var animator = normalized.AddComponent<Animator>();
                var vrmHuman = go.GetComponent<VRMHumanoidDescription>();
                var avatarDescription = AvatarDescription.Create();
                if (vrmHuman != null && vrmHuman.Description != null)
                {
                    avatarDescription.armStretch = vrmHuman.Description.armStretch;
                    avatarDescription.legStretch = vrmHuman.Description.legStretch;
                    avatarDescription.upperArmTwist = vrmHuman.Description.upperArmTwist;
                    avatarDescription.lowerArmTwist = vrmHuman.Description.lowerArmTwist;
                    avatarDescription.upperLegTwist = vrmHuman.Description.upperLegTwist;
                    avatarDescription.lowerLegTwist = vrmHuman.Description.lowerLegTwist;
                    avatarDescription.feetSpacing = vrmHuman.Description.feetSpacing;
                    avatarDescription.hasTranslationDoF = vrmHuman.Description.hasTranslationDoF;
                }
                avatarDescription.SetHumanBones(map);
                var avatar = avatarDescription.CreateAvatar(normalized.transform);

                avatar.name = go.name + ".normalized";
                animator.avatar = avatar;

                var humanPoseTransfer = normalized.AddComponent<HumanPoseTransfer>();
                humanPoseTransfer.Avatar = avatar;
            }

            //
            // 各メッシュから回転・スケールを取り除いてBinding行列を再計算する
            //
            foreach (var src in go.transform.Traverse())
            {
                var dst = boneMap[src];

                {
                    //
                    // SkinnedMesh
                    //
                    var srcRenderer = src.GetComponent<SkinnedMeshRenderer>();
                    if (srcRenderer != null && srcRenderer.enabled)
                    {
                        // clear blendShape
                        var srcMesh = srcRenderer.sharedMesh;
                        for (int i = 0; i < srcMesh.blendShapeCount; ++i)
                        {
                            srcRenderer.SetBlendShapeWeight(i, 0);
                        }

                        var mesh = new Mesh();
                        mesh.name = srcMesh.name + ".baked";
                        srcRenderer.BakeMesh(mesh);

                        //var m = src.localToWorldMatrix;
                        var m = default(Matrix4x4);
                        m.SetTRS(Vector3.zero, src.rotation, Vector3.one);

                        mesh.vertices = mesh.vertices.Select(x => m.MultiplyPoint(x)).ToArray();
                        mesh.normals = mesh.normals.Select(x => m.MultiplyVector(x).normalized).ToArray();

                        mesh.uv = srcMesh.uv;
                        mesh.tangents = srcMesh.tangents;
                        mesh.subMeshCount = srcMesh.subMeshCount;
                        for (int i = 0; i < srcMesh.subMeshCount; ++i)
                        {
                            mesh.SetIndices(srcMesh.GetIndices(i), srcMesh.GetTopology(i), i);
                        }
                        mesh.boneWeights = srcMesh.boneWeights;

                        var meshVertices = mesh.vertices;
                        var blendShapeMesh = new Mesh();
                        for (int i = 0; i < srcMesh.blendShapeCount; ++i)
                        {
                            srcRenderer.SetBlendShapeWeight(i, 100.0f);
                            srcRenderer.BakeMesh(blendShapeMesh);
                            srcRenderer.SetBlendShapeWeight(i, 0);

                            if (blendShapeMesh.vertices.Length!=mesh.vertices.Length){
                                throw new Exception("diffrent vertex count");
                            }

                            var weight = srcMesh.GetBlendShapeFrameWeight(i, 0);
                            var vertices = blendShapeMesh.vertices;
                            var normals = blendShapeMesh.normals;
                            var tangents = blendShapeMesh.tangents.Select(x => (Vector3)x).ToArray();

                            for (int j = 0; j < vertices.Length; ++j) {
                                vertices[j]= m.MultiplyPoint(vertices[j]) - meshVertices[j];
                            }
                            for (int j = 0; j < normals.Length; ++j)
                            {
                                normals[j] = m.MultiplyVector(normals[j]).normalized;
                            }
                            for(int j=0; j<tangents.Length; ++j)
                            {
                                tangents[j] = m.MultiplyVector(tangents[j]).normalized;
                            }

                            var name = srcMesh.GetBlendShapeName(i);
                            if (string.IsNullOrEmpty(name))
                            {
                                name = String.Format("{0}", i);
                            }

                            try
                            {
                                mesh.AddBlendShapeFrame(name,
                                    weight,
                                    vertices.Length == meshVertices.Length ? vertices : null,
                                    normals.Length == meshVertices.Length ? normals : null,
                                    tangents.Length == meshVertices.Length ? tangents : null
                                    );
                            }
                            catch (Exception)
                            {
                                Debug.LogWarningFormat("{0}.{1}", mesh.name, srcMesh.GetBlendShapeName(i));

                                throw;
                            }
                        }

                        // recalc bindposes
                        var bones = srcRenderer.bones.Select(x => boneMap[x]).ToArray();
                        mesh.bindposes = bones.Select(x =>
                            x.worldToLocalMatrix * dst.transform.localToWorldMatrix).ToArray();

                        mesh.RecalculateBounds();

                        var dstRenderer = dst.gameObject.AddComponent<SkinnedMeshRenderer>();
                        dstRenderer.sharedMaterials = srcRenderer.sharedMaterials;
                        dstRenderer.sharedMesh = mesh;
                        dstRenderer.bones = bones;
                        if (srcRenderer.rootBone != null)
                        {
                            dstRenderer.rootBone = boneMap[srcRenderer.rootBone];
                        }
                    }
                }

                {
                    //
                    // not SkinnedMesh
                    //
                    var srcFilter = src.GetComponent<MeshFilter>();
                    if (srcFilter != null)
                    {
                        var srcRenderer = src.GetComponent<MeshRenderer>();
                        if (srcRenderer!=null && srcRenderer.enabled)
                        {
                            var dstFilter = dst.gameObject.AddComponent<MeshFilter>();
                            dstFilter.sharedMesh = srcFilter.sharedMesh;

                            var dstRenderer = dst.gameObject.AddComponent<MeshRenderer>();
                            dstRenderer.sharedMaterials = srcRenderer.sharedMaterials;
                        }
                    }
                }
            }

            return normalized;
        }
    }
}
