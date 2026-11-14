/*
 *  Copyright 2025 Hongyu Cao
 *
 *  Licensed under the Apache License, Version 2.0 (the "License");
 *  you may not use this file except in compliance with the License.
 *  You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YOPO.SIM {
    // Randomly places puddles (flat, irregularly shaped water surfaces) on flat ground.
    // The terrain is NEVER modified: spots are filtered by flatness (maxHeightDiff)
    // so a horizontal zero-thickness water sheet at minGround + surfaceOffset reads
    // as a natural puddle (low spots submerged, high spots emerge as the shoreline).
    // EvaluationScene: randomized once at Start with a fixed seed (reproducible), after
    // waiting for Vista's asynchronous terrain generation to settle.
    // DataGenerationScene: listens to DataGenerationEvents.BeforePoissonGeneration and
    // re-rolls the layout once per terrain round, so every terrain gets its own puddles.
    public class PuddleRandomizer : MonoBehaviour {
        [Header("Puddle")]
        [SerializeField] private GameObject _puddlePrefab;   // provides the water material via its MeshRenderer
        [SerializeField] private int _count = 6;
        [SerializeField] private Vector2 _radiusRange = new Vector2(1.5f, 4.5f);
        [SerializeField] private float _surfaceOffset = 0.08f; // water surface this far above the LOWEST original ground point; raise it to lift the sheet out of the grass, lower it if edges look floaty

        [Header("Placement")]
        [SerializeField] private Terrain _terrain;             // falls back to Terrain.activeTerrain
        [SerializeField] private Vector2 _areaMin = new Vector2(7f, 7f);   // xz rect, covers the hand-placed puddles
        [SerializeField] private Vector2 _areaMax = new Vector2(62f, 38f);
        [SerializeField] private float _maxSlopeDeg = 10f;
        [SerializeField] private float _maxHeightDiff = 0.05f; // max ground height variation across the puddle; tight so a dig-free water sheet still hugs the ground
        [SerializeField] private float _minGap = 1f;           // min water-edge distance between puddles
        [SerializeField] private Vector2 _exclusionCenter = new Vector2(2.24f, 5.28f); // robot spawn (x, z)
        [SerializeField] private float _exclusionRadius = 8f;
        [SerializeField] private int _maxAttemptsPerPuddle = 60;

        [Header("Randomization")]
        [SerializeField] private int _seed = 0;
        [SerializeField] private bool _randomizeOnStart = true;
        [SerializeField] private bool _reRollPerTerrainRound = true; // used in DataGenerationScene

        // Irregular puddle outline (low-frequency sinusoid wobble) used by the
        // water mesh, giving an organic puddle shape instead of a perfect circle.
        private struct BlobOutline {
            public float radius, amp1, amp2, phase1, phase2;

            public float RadiusAt(float angle) {
                return radius * (1f + amp1 * Mathf.Sin(2f * angle + phase1)
                                      + amp2 * Mathf.Sin(3f * angle + phase2));
            }

            public static BlobOutline CreateRandom(float radius) {
                return new BlobOutline {
                    radius = radius,
                    amp1 = UnityEngine.Random.Range(0.06f, 0.15f),
                    amp2 = UnityEngine.Random.Range(0.04f, 0.10f),
                    phase1 = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                    phase2 = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                };
            }
        }

        private readonly List<GameObject> _puddles = new();
        private readonly List<Vector3> _spots = new(); // x, z, radius of placed puddles
        private int _layoutIndex = 0;

        void OnEnable() {
            DataGenerationEvents.BeforePoissonGeneration += OnBeforePoissonGeneration;
        }

        void OnDisable() {
            DataGenerationEvents.BeforePoissonGeneration -= OnBeforePoissonGeneration;
        }

        IEnumerator Start() {
            if (!_randomizeOnStart) {
                yield break;
            }
            yield return WaitForTerrainSettled();
            Generate(_seed);
        }

        // Vista regenerates the terrain asynchronously at play start, so heights sampled
        // in the first frames are stale. Wait until probe heights stop changing.
        private IEnumerator WaitForTerrainSettled() {
            Terrain terrain = _terrain != null ? _terrain : Terrain.activeTerrain;
            if (terrain == null) {
                yield break;
            }
            float[] previous = null;
            int stableFrames = 0;
            for (int frame = 0; frame < 600 && stableFrames < 5; frame++) {
                float[] probe = ProbeHeights(terrain);
                if (previous != null && ProbeEquals(previous, probe)) {
                    stableFrames++;
                }
                else {
                    stableFrames = 0;
                }
                previous = probe;
                yield return null;
            }
        }

        private float[] ProbeHeights(Terrain terrain) {
            float[] probe = new float[9];
            Vector3 terrainPos = terrain.GetPosition();
            for (int i = 0; i < 9; i++) {
                float x = Mathf.Lerp(_areaMin.x, _areaMax.x, (i % 3) * 0.5f);
                float z = Mathf.Lerp(_areaMin.y, _areaMax.y, (i / 3) * 0.5f);
                probe[i] = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrainPos.y;
            }
            return probe;
        }

        private static bool ProbeEquals(float[] a, float[] b) {
            for (int i = 0; i < a.Length; i++) {
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f) {
                    return false;
                }
            }
            return true;
        }

        private void OnBeforePoissonGeneration(object sender, EventArgs args) {
            if (!_reRollPerTerrainRound) {
                return;
            }
            _layoutIndex++;
            Generate(_seed + _layoutIndex);
        }

        // A spot that is too bumpy for a large blob can still host a smaller
        // one, so each sample is retried with shrinking radii before moving on.
        private static readonly float[] ShrinkSteps = { 1f, 0.7f, 0.5f, 0.35f };

        public void Generate(int seed) {
            Clear();
            Terrain terrain = _terrain != null ? _terrain : Terrain.activeTerrain;
            if (_puddlePrefab == null || terrain == null) {
                Debug.LogWarning("PuddleRandomizer: prefab or terrain not set, skipping.");
                return;
            }
            UnityEngine.Random.InitState(seed);
            TerrainData data = terrain.terrainData;
            Vector3 terrainPos = terrain.GetPosition();
            Debug.Log($"PuddleRandomizer: terrain '{terrain.name}' pos={terrainPos} size={data.size}");
            for (int placed = 0; placed < _count; placed++) {
                for (int attempt = 0; attempt < _maxAttemptsPerPuddle; attempt++) {
                    float x = UnityEngine.Random.Range(_areaMin.x, _areaMax.x);
                    float z = UnityEngine.Random.Range(_areaMin.y, _areaMax.y);
                    float radius = UnityEngine.Random.Range(_radiusRange.x, _radiusRange.y);
                    bool done = false;
                    foreach (float scale in ShrinkSteps) {
                        float r = radius * scale;
                        if (!IsSpotValid(terrain, data, terrainPos, x, z, r, out float waterY)) {
                            continue;
                        }
                        BlobOutline outline = BlobOutline.CreateRandom(r);
                        GameObject puddle = Instantiate(_puddlePrefab, new Vector3(x, waterY, z),
                            Quaternion.identity, transform);
                        puddle.transform.localScale = Vector3.one;
                        // Replace the prefab's mesh with a flat, irregular blob so the puddle
                        // is a horizontal zero-thickness surface instead of a round cylinder.
                        MeshFilter meshFilter = puddle.GetComponent<MeshFilter>();
                        if (meshFilter != null) {
                            meshFilter.sharedMesh = CreateBlobMesh(outline);
                        }
                        _puddles.Add(puddle);
                        _spots.Add(new Vector3(x, z, r));
                        Debug.Log($"PuddleRandomizer: puddle {_puddles.Count} at ({x:F1}, {z:F1}) r={r:F1} waterY={waterY:F2}");
                        done = true;
                        break;
                    }
                    if (done) {
                        break;
                    }
                }
            }
            Debug.Log($"PuddleRandomizer: placed {_puddles.Count}/{_count} puddles (seed {seed}).");
        }

        private bool IsSpotValid(Terrain terrain, TerrainData data, Vector3 terrainPos,
                                 float x, float z, float radius, out float waterY) {
            waterY = 0f;
            if (Vector2.Distance(new Vector2(x, z), _exclusionCenter) < _exclusionRadius) {
                return false;
            }
            float nx = (x - terrainPos.x) / data.size.x;
            float nz = (z - terrainPos.z) / data.size.z;
            if (data.GetSteepness(nx, nz) > _maxSlopeDeg) {
                return false;
            }
            // Keep puddles apart: reject if the new circle overlaps an existing one.
            foreach (Vector3 spot in _spots) {
                float edgeGap = Vector2.Distance(new Vector2(x, z), new Vector2(spot.x, spot.y))
                                - spot.z - radius;
                if (edgeGap < _minGap) {
                    return false;
                }
            }
            // Sample the ground at the center and 8 edge points. The flatness check
            // keeps the footprint nearly even, so a horizontal water sheet at
            // minHeight + surfaceOffset submerges the lows and lets the highs
            // emerge, reading as a natural shoreline instead of a floating slab.
            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;
            for (int s = 0; s < 9; s++) {
                float angle = (s - 1) * Mathf.PI * 0.25f;
                float sx = s == 0 ? x : x + Mathf.Cos(angle) * radius;
                float sz = s == 0 ? z : z + Mathf.Sin(angle) * radius;
                float h = terrain.SampleHeight(new Vector3(sx, 0f, sz)) + terrainPos.y;
                minHeight = Mathf.Min(minHeight, h);
                maxHeight = Mathf.Max(maxHeight, h);
            }
            if (maxHeight - minHeight > _maxHeightDiff) {
                return false;
            }
            waterY = minHeight + _surfaceOffset;
            return true;
        }

        // Flat fan mesh built from the outline, giving an organic puddle shape
        // instead of a perfect circle.
        private static Mesh CreateBlobMesh(BlobOutline outline) {
            const int segments = 32;
            Vector3[] vertices = new Vector3[segments + 1];
            int[] triangles = new int[segments * 3];
            vertices[0] = Vector3.zero;
            for (int i = 0; i < segments; i++) {
                float angle = (float)i / segments * Mathf.PI * 2f;
                float r = outline.RadiusAt(angle);
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
                triangles[i * 3] = 0;
                // Wound so the face normal points +Y (up): the second index is
                // the NEXT rim vertex, wrapping back to 1 after the last segment.
                triangles[i * 3 + 1] = (i + 1) % segments + 1;
                triangles[i * 3 + 2] = i + 1;
            }
            Mesh mesh = new Mesh { vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Clear() {
            foreach (GameObject puddle in _puddles) {
                if (puddle != null) {
                    Destroy(puddle);
                }
            }
            _puddles.Clear();
            _spots.Clear();
        }
    }
}
