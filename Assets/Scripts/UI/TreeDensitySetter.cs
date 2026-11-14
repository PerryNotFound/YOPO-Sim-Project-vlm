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

#if VISTA
using System;
using System.Collections;
using Pinwheel.Vista;
using Pinwheel.Vista.Graph;
using TMPro;
using UnityEngine;

namespace YOPO.SIM.UI {
    public class TreeDensitySetter : MonoBehaviour {
        [SerializeField] private TMP_Dropdown _densityDropdown;
        [SerializeField] private TerrainGraph _denseTreeGraph;
        [SerializeField] private TerrainGraph _normalTreeGraph;
        [SerializeField] private TerrainGraph _sparseTreeGraph;
        [SerializeField] private VistaManager _vistaManager;
        [SerializeField] private LocalProceduralBiome _biome;
        [SerializeField] private bool _isDebug = false;

        void Start() {
            _densityDropdown.onValueChanged.AddListener(OnDensityChanged);
        }

        void Update() {
            if (_isDebug && Input.GetKeyDown(KeyCode.Alpha1)) {
                _densityDropdown.value = 0; // Dense
            }
            else if (_isDebug && Input.GetKeyDown(KeyCode.Alpha2)) {
                _densityDropdown.value = 1; // Normal
            }
            else if (_isDebug && Input.GetKeyDown(KeyCode.Alpha3)) {
                _densityDropdown.value = 2; // Sparse
            }
        }

        private void OnDensityChanged(int arg0) {
            Debug.Log($"Selected density: {arg0}");
            _biome.terrainGraph = arg0 switch {
                0 => _denseTreeGraph,
                1 => _normalTreeGraph,
                2 => _sparseTreeGraph,
                _ => _normalTreeGraph,
            };
            StartCoroutine(GenerateTerrain());
        }


        private IEnumerator GenerateTerrain() {
            // The three density graphs only differ in tree parameters, but Vista
            // regenerates the whole tile and overwrites the baked heightmap.
            // Snapshot the heights first so switching density only changes trees
            // and the ground under the robot's spawn point stays put.
            var heightSnapshots = new System.Collections.Generic.List<(Terrain terrain, int resolution, float[,] heights)>();
            foreach (ITile tile in _vistaManager.GetTiles()) {
                if (tile.gameObject.TryGetComponent(out Terrain terrain) && terrain.terrainData != null) {
                    int resolution = terrain.terrainData.heightmapResolution;
                    heightSnapshots.Add((terrain, resolution, terrain.terrainData.GetHeights(0, 0, resolution, resolution)));
                }
            }
            // Wait for the terrain to be generated
            var generationTask = _vistaManager.Generate(_vistaManager.GetTiles());
            yield return new WaitUntil(() => generationTask.isCompleted);
            // Restore the baked heightmaps and re-ground the newly placed trees
            foreach (var (terrain, resolution, heights) in heightSnapshots) {
                TerrainData terrainData = terrain.terrainData;
                if (terrainData.heightmapResolution != resolution) {
                    terrainData.heightmapResolution = resolution;
                }
                terrainData.SetHeights(0, 0, heights);
                GroundTreesOnRestoredHeights(terrainData);
            }
            // Refresh colliders after generation
            RefreshTerrainColliders();
        }

        private static void GroundTreesOnRestoredHeights(TerrainData terrainData) {
            TreeInstance[] trees = terrainData.treeInstances;
            for (int i = 0; i < trees.Length; i++) {
                TreeInstance tree = trees[i];
                Vector3 position = tree.position;
                position.y = terrainData.GetInterpolatedHeight(position.x, position.z) / terrainData.size.y;
                tree.position = position;
                trees[i] = tree;
            }
            terrainData.treeInstances = trees;
        }
        private void RefreshTerrainColliders() {
            foreach (ITile tile in _vistaManager.GetTiles()) {
                if (tile.gameObject.TryGetComponent(out TerrainCollider terrainCollider)) {
                    terrainCollider.enabled = false;
                    terrainCollider.enabled = true;
                }
            }
        }
    }
}
#endif