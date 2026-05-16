using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ProceduralTree
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ProceduralTree : MonoBehaviour
    {
        #region Properties
        [SerializeField, Min(1)] private int seed = 12345;

        [SerializeField, Min(1f)] private float treeHeight = 7f;
        [SerializeField, Min(0.05f)] private float trunkRadius = 0.3f;
        [SerializeField, Range(0f, 12f)] private float trunkLean = 4f;
        [SerializeField, Range(0f, 20f)] private float trunkCurvature = 4f;
        [SerializeField, Range(0.1f, 0.9f)] private float branchStartHeight = 0.3f;

        [SerializeField, Min(3)] private int radialSegments = 10;
        [SerializeField, Min(2)] private int trunkSegments = 10;
        [SerializeField, Min(2)] private int branchSegments = 5;
        [SerializeField, Range(1, 4)] private int maxBranchDepth = 3;

        [SerializeField] private Vector2Int mainBranchCountRange = new Vector2Int(7, 10);
        [SerializeField] private Vector2Int childBranchCountRange = new Vector2Int(2, 4);

        [SerializeField] private Vector2 mainBranchLengthRange = new Vector2(0.28f, 0.42f);
        [SerializeField, Range(0.2f, 0.95f)] private float childBranchLengthScale = 0.68f;
        [SerializeField, Range(0.2f, 0.95f)] private float childBranchRadiusScale = 0.58f;
        [SerializeField, Range(0f, 35f)] private float branchCurvature = 12f;
        [SerializeField, Range(0f, 1f)] private float branchUpwardBias = 0.42f;
        [SerializeField, Range(0f, 35f)] private float branchTwistJitter = 14f;
        [SerializeField, Min(0.01f)] private float minimumBranchLength = 0.4f;
        [SerializeField, Min(0.005f)] private float minimumBranchRadius = 0.02f;

        [SerializeField, Min(1)] private int branchLeafCards = 4;
        [SerializeField, Min(1)] private int twigLeafCards = 6;
        [SerializeField] private Vector2 leafWidthRange = new Vector2(0.22f, 0.34f);
        [SerializeField] private Vector2 leafHeightRange = new Vector2(0.26f, 0.42f);
        [SerializeField, Range(0f, 0.95f)] private float leafStartAlongBranch = 0.2f;
        [SerializeField, Min(0f)] private float leafClusterRadius = 0.35f;
        [SerializeField, Range(0f, 35f)] private float leafTiltJitter = 18f;
        [SerializeField, Range(0f, 1f)] private float leafVerticalBias = 0.55f;

        [SerializeField, Min(1)] private int leafSeed = 54321;
        [SerializeField, Range(0f, 1f)] private float leafPositionVariance = 0.45f;
        [SerializeField, Range(0f, 45f)] private float leafYawVariance = 24f;
        [SerializeField, Range(0f, 45f)] private float leafRollVariance = 18f;
        [SerializeField, Min(0f)] private float leafRootPushIn = 0.08f;
        [SerializeField, Min(0f)] private float leafRootBackOffset = 0.04f;
        [SerializeField, Range(0f, 1f)] private float leafDirectionAlignment = 0.82f;
        [SerializeField, Range(0f, 1f)] private float leafNormalAlignment = 0.75f;

        [SerializeField] private Vector2Int twigBranchCountRange = new Vector2Int(3, 6);
        [SerializeField] private Vector2 twigLengthRange = new Vector2(0.16f, 0.3f);
        [SerializeField, Min(2)] private int twigSegments = 3;
        [SerializeField, Range(0.1f, 0.8f)] private float twigRadiusScale = 0.32f;
        [SerializeField, Range(0f, 30f)] private float twigSpread = 16f;

        [SerializeField, Range(0.5f, 3f)] private float leafAnchorDensity = 1.35f;
        [SerializeField, Range(0.5f, 3f)] private float leafCardDensity = 1.3f;
        [SerializeField, Range(0.5f, 2f)] private float terminalLeafDensity = 1.35f;
        [SerializeField, Range(0.5f, 2f)] private float twigLeafDensity = 1.15f;

        [SerializeField, Min(0.01f)] private float barkUvScale = 1.5f;

        [SerializeField] private bool useSingleSidedLeafGeometry = true;

        [SerializeField, Min(0.005f)] private float minimumRenderableBranchRadius = 0.03f;
        [SerializeField, Min(0.005f)] private float minimumRenderableTwigRadius = 0.018f;
        [SerializeField, Min(0.005f)] private float minimumLeafAnchorRadius = 0.02f;
        [SerializeField, Min(0.005f)] private float minimumCapRadius = 0.018f;

        [SerializeField, Range(0.1f, 1f)] private float surfaceAnchorLeafCardScale = 0.55f;
        [SerializeField, Range(0.1f, 1f)] private float twigAnchorDensityScale = 0.45f;

        [SerializeField, Range(1, 4)] private int maximumRenderableBranchDepth = 2;
        [SerializeField] private bool renderTwigGeometry = false;

        [SerializeField, Range(0.1f, 1f)] private float surfaceAnchorDensityScale = 0.3f;
        [SerializeField, Range(0.5f, 3f)] private float terminalLeafCardDensityScale = 1.8f;
        [SerializeField, Range(0.5f, 2f)] private float terminalLeafSizeScale = 1.3f;
        [SerializeField, Range(0.5f, 2f)] private float canopyClusterRadiusScale = 1.35f;

        [SerializeField, Range(1, 6)] private int hiddenBranchLeafTipCount = 3;
        [SerializeField, Range(0.5f, 3f)] private float hiddenBranchLeafDensity = 1.45f;

        [SerializeField] private bool enableLods = true;
        [SerializeField, Min(0.1f)] private float lod1Distance = 18f;
        [SerializeField, Min(0.1f)] private float lod2Distance = 36f;
        [SerializeField, Min(0.1f)] private float lodCullDistance = 60f;
        [SerializeField, Range(20f, 120f)] private float lodReferenceFieldOfView = 60f;

        [SerializeField, HideInInspector] private GameObject lod1Object;
        [SerializeField, HideInInspector] private GameObject lod2Object;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;

        private LODGroup lodGroup;

        private Mesh lod1Mesh;
        private Mesh lod2Mesh;

        private MeshFilter lod1MeshFilter;
        private MeshRenderer lod1MeshRenderer;
        private MeshFilter lod2MeshFilter;
        private MeshRenderer lod2MeshRenderer;

#if UNITY_EDITOR
        private bool generationQueued;
#endif
        #endregion

        #region Structs
        private struct BranchPoint
        {
            public Vector3 position;
            public Vector3 tangent;
            public Vector3 normal;
            public float radius;
            public float v;
        }

        private struct BranchTip
        {
            public Vector3 position;
            public Vector3 direction;
            public Vector3 normal;
            public float size;
            public float density;
            public float spread;
            public bool isTwig;
            public bool isTerminal;
            public bool isSurfaceAnchor;
            public float windWeight;
        }

        private struct GenerationSettingsSnapshot
        {
            public int radialSegments;
            public int trunkSegments;
            public int branchSegments;
            public int twigSegments;
            public int maxBranchDepth;
            public Vector2Int mainBranchCountRange;
            public Vector2Int childBranchCountRange;
            public Vector2Int twigBranchCountRange;
            public int branchLeafCards;
            public int twigLeafCards;
        }
        #endregion

        private void OnEnable()
        {
            RequestGenerate();
        }

        private void OnValidate()
        {
            ClampValues();
            RequestGenerate();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.delayCall -= DelayedGenerate;
            generationQueued = false;
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            EditorApplication.delayCall -= DelayedGenerate;
            generationQueued = false;
#endif

            ReleaseMesh(ref generatedMesh, meshFilter);
            ReleaseMesh(ref lod1Mesh, lod1MeshFilter);
            ReleaseMesh(ref lod2Mesh, lod2MeshFilter);
        }

        #region Methods
        [ContextMenu("Regenerate")]
        private void Regenerate()
        {
            RequestGenerate();
        }

        private void RequestGenerate()
        {
            EnsureComponents();

            if (meshFilter == null || meshRenderer == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (generationQueued)
            {
                return;
            }

            generationQueued = true;
            EditorApplication.delayCall -= DelayedGenerate;
            EditorApplication.delayCall += DelayedGenerate;
#else
            GenerateImmediate();
#endif
        }

#if UNITY_EDITOR
        private void DelayedGenerate()
        {
            EditorApplication.delayCall -= DelayedGenerate;
            generationQueued = false;

            if (this == null)
            {
                return;
            }

            if (!isActiveAndEnabled)
            {
                return;
            }

            GenerateImmediate();
        }
#endif

        private void GenerateImmediate()
        {
            ClampValues();
            EnsureComponents();

            if (meshFilter == null || meshRenderer == null)
            {
                return;
            }

            GenerateTreeMesh(ref generatedMesh, "Procedural Tree Mesh", 1f);
            meshFilter.sharedMesh = generatedMesh;

            if (enableLods)
            {
                EnsureLodObjects();

                GenerateTreeMesh(ref lod1Mesh, "Procedural Tree Mesh LOD1", 0.5f);
                GenerateTreeMesh(ref lod2Mesh, "Procedural Tree Mesh LOD2", 0.25f);

                if (lod1MeshFilter != null)
                {
                    lod1MeshFilter.sharedMesh = lod1Mesh;
                }

                if (lod2MeshFilter != null)
                {
                    lod2MeshFilter.sharedMesh = lod2Mesh;
                }

                SyncLodRendererSettings(lod1MeshRenderer);
                SyncLodRendererSettings(lod2MeshRenderer);

                Vector3 boundsSize = generatedMesh != null ? generatedMesh.bounds.size : new Vector3(treeHeight, treeHeight, treeHeight);
                ConfigureLodGroup(boundsSize);
            }
            else
            {
                CleanupLodObjects(true);
            }
        }

        private void EnsureComponents()
        {
            if (meshFilter == null)
            {
                meshFilter = GetComponent<MeshFilter>();
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
            }

            if (lodGroup == null)
            {
                lodGroup = GetComponent<LODGroup>();
            }

            CacheLodChildComponents();
        }

        private void ClampValues()
        {
            seed = Mathf.Max(1, seed);

            treeHeight = Mathf.Max(1f, treeHeight);
            trunkRadius = Mathf.Max(0.05f, trunkRadius);
            trunkLean = Mathf.Clamp(trunkLean, 0f, 12f);
            trunkCurvature = Mathf.Clamp(trunkCurvature, 0f, 20f);
            branchStartHeight = Mathf.Clamp(branchStartHeight, 0.1f, 0.9f);

            radialSegments = Mathf.Max(3, radialSegments);
            trunkSegments = Mathf.Max(2, trunkSegments);
            branchSegments = Mathf.Max(2, branchSegments);
            maxBranchDepth = Mathf.Clamp(maxBranchDepth, 1, 4);

            mainBranchCountRange.x = Mathf.Max(1, mainBranchCountRange.x);
            mainBranchCountRange.y = Mathf.Max(mainBranchCountRange.x, mainBranchCountRange.y);

            childBranchCountRange.x = Mathf.Max(0, childBranchCountRange.x);
            childBranchCountRange.y = Mathf.Max(childBranchCountRange.x, childBranchCountRange.y);

            mainBranchLengthRange.x = Mathf.Clamp(mainBranchLengthRange.x, 0.05f, 1f);
            mainBranchLengthRange.y = Mathf.Clamp(mainBranchLengthRange.y, mainBranchLengthRange.x, 1f);

            childBranchLengthScale = Mathf.Clamp(childBranchLengthScale, 0.2f, 0.95f);
            childBranchRadiusScale = Mathf.Clamp(childBranchRadiusScale, 0.2f, 0.95f);
            branchCurvature = Mathf.Clamp(branchCurvature, 0f, 35f);
            branchUpwardBias = Mathf.Clamp01(branchUpwardBias);
            branchTwistJitter = Mathf.Clamp(branchTwistJitter, 0f, 35f);
            minimumBranchLength = Mathf.Max(0.01f, minimumBranchLength);
            minimumBranchRadius = Mathf.Max(0.005f, minimumBranchRadius);

            branchLeafCards = Mathf.Max(1, branchLeafCards);
            twigLeafCards = Mathf.Max(1, twigLeafCards);
            leafWidthRange.x = Mathf.Max(0.01f, leafWidthRange.x);
            leafWidthRange.y = Mathf.Max(leafWidthRange.x, leafWidthRange.y);
            leafHeightRange.x = Mathf.Max(0.01f, leafHeightRange.x);
            leafHeightRange.y = Mathf.Max(leafHeightRange.x, leafHeightRange.y);
            leafStartAlongBranch = Mathf.Clamp(leafStartAlongBranch, 0f, 0.95f);
            leafClusterRadius = Mathf.Max(0f, leafClusterRadius);
            leafTiltJitter = Mathf.Clamp(leafTiltJitter, 0f, 35f);
            leafVerticalBias = Mathf.Clamp01(leafVerticalBias);

            leafSeed = Mathf.Max(1, leafSeed);
            leafPositionVariance = Mathf.Clamp01(leafPositionVariance);
            leafYawVariance = Mathf.Clamp(leafYawVariance, 0f, 45f);
            leafRollVariance = Mathf.Clamp(leafRollVariance, 0f, 45f);
            leafRootPushIn = Mathf.Max(0f, leafRootPushIn);
            leafRootBackOffset = Mathf.Max(0f, leafRootBackOffset);
            leafDirectionAlignment = Mathf.Clamp01(leafDirectionAlignment);
            leafNormalAlignment = Mathf.Clamp01(leafNormalAlignment);

            twigBranchCountRange.x = Mathf.Max(1, twigBranchCountRange.x);
            twigBranchCountRange.y = Mathf.Max(twigBranchCountRange.x, twigBranchCountRange.y);
            twigLengthRange.x = Mathf.Clamp(twigLengthRange.x, 0.05f, 1f);
            twigLengthRange.y = Mathf.Clamp(twigLengthRange.y, twigLengthRange.x, 1f);
            twigSegments = Mathf.Max(2, twigSegments);
            twigRadiusScale = Mathf.Clamp(twigRadiusScale, 0.1f, 0.8f);
            twigSpread = Mathf.Clamp(twigSpread, 0f, 30f);

            leafAnchorDensity = Mathf.Clamp(leafAnchorDensity, 0.5f, 3f);
            leafCardDensity = Mathf.Clamp(leafCardDensity, 0.5f, 3f);
            terminalLeafDensity = Mathf.Clamp(terminalLeafDensity, 0.5f, 2f);
            twigLeafDensity = Mathf.Clamp(twigLeafDensity, 0.5f, 2f);

            barkUvScale = Mathf.Max(0.01f, barkUvScale);

            minimumRenderableBranchRadius = Mathf.Max(0.005f, minimumRenderableBranchRadius);
            minimumRenderableTwigRadius = Mathf.Max(0.005f, minimumRenderableTwigRadius);
            minimumLeafAnchorRadius = Mathf.Max(0.005f, minimumLeafAnchorRadius);
            minimumCapRadius = Mathf.Max(0.005f, minimumCapRadius);

            surfaceAnchorLeafCardScale = Mathf.Clamp(surfaceAnchorLeafCardScale, 0.1f, 1f);
            twigAnchorDensityScale = Mathf.Clamp(twigAnchorDensityScale, 0.1f, 1f);

            maximumRenderableBranchDepth = Mathf.Clamp(maximumRenderableBranchDepth, 1, 4);

            surfaceAnchorDensityScale = Mathf.Clamp(surfaceAnchorDensityScale, 0.1f, 1f);
            terminalLeafCardDensityScale = Mathf.Clamp(terminalLeafCardDensityScale, 0.5f, 3f);
            terminalLeafSizeScale = Mathf.Clamp(terminalLeafSizeScale, 0.5f, 2f);
            canopyClusterRadiusScale = Mathf.Clamp(canopyClusterRadiusScale, 0.5f, 2f);

            hiddenBranchLeafTipCount = Mathf.Clamp(hiddenBranchLeafTipCount, 1, 6);
            hiddenBranchLeafDensity = Mathf.Clamp(hiddenBranchLeafDensity, 0.5f, 3f);

            lod1Distance = Mathf.Max(0.1f, lod1Distance);
            lod2Distance = Mathf.Max(lod1Distance + 0.1f, lod2Distance);
            lodCullDistance = Mathf.Max(lod2Distance + 0.1f, lodCullDistance);
            lodReferenceFieldOfView = Mathf.Clamp(lodReferenceFieldOfView, 20f, 120f);
        }

        private void GenerateBranchesFromTrunk(
            List<BranchPoint> trunkPath,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles,
            List<BranchTip> branchTips)
        {
            int mainBranchCount = RandomRangeInclusive(mainBranchCountRange);
            float spiralOffset = Random.Range(0f, 360f);

            for (int i = 0; i < mainBranchCount; i++)
            {
                float normalizedHeight = Mathf.Lerp(
                    branchStartHeight,
                    0.96f,
                    (i + Random.Range(0.15f, 0.95f)) / mainBranchCount);

                int pathIndex = Mathf.Clamp(
                    Mathf.RoundToInt(normalizedHeight * (trunkPath.Count - 1)),
                    1,
                    trunkPath.Count - 2);

                BranchPoint spawnPoint = trunkPath[pathIndex];
                Vector3 tangent = spawnPoint.tangent;

                float angle = spiralOffset + i * 137.5f + Random.Range(-18f, 18f);
                Vector3 outward = Quaternion.AngleAxis(angle, tangent) * GetStablePerpendicular(tangent);
                outward = Vector3.ProjectOnPlane(outward, tangent).normalized;

                if (outward.sqrMagnitude < 0.0001f)
                {
                    outward = GetStablePerpendicular(tangent);
                }

                float branchLength = treeHeight
                    * Random.Range(mainBranchLengthRange.x, mainBranchLengthRange.y)
                    * Mathf.Lerp(1f, 0.58f, normalizedHeight);

                float branchRadius = spawnPoint.radius
                    * Mathf.Lerp(0.52f, 0.34f, normalizedHeight)
                    * Random.Range(0.9f, 1.08f);

                branchRadius = Mathf.Max(minimumBranchRadius, branchRadius);

                Vector3 startDirection = Vector3.Slerp(
                    outward,
                    tangent,
                    Mathf.Lerp(0.32f, 0.72f, normalizedHeight)).normalized;

                float startEmbed = Mathf.Min(spawnPoint.radius * 0.35f, branchRadius * 1.25f);
                Vector3 startPosition = spawnPoint.position + outward * (spawnPoint.radius - startEmbed);

                GenerateBranchRecursive(
                    startPosition,
                    startDirection,
                    branchLength,
                    branchRadius,
                    1,
                    vertices,
                    normals,
                    uvs,
                    colors,
                    triangles,
                    branchTips,
                    0f);
            }

            BranchTip topTip = new BranchTip();
            topTip.position = trunkPath[trunkPath.Count - 1].position;
            topTip.direction = trunkPath[trunkPath.Count - 1].tangent;
            topTip.normal = trunkPath[trunkPath.Count - 1].normal;
            topTip.size = Mathf.Clamp(treeHeight * 0.035f, 0.08f, 0.18f);
            topTip.density = 0.65f;
            topTip.spread = 0.32f;
            topTip.isTwig = false;
            topTip.isTerminal = true;
            topTip.isSurfaceAnchor = false;
            topTip.windWeight = 0f;
            branchTips.Add(topTip);
        }

        private void GenerateBranchRecursive(
            Vector3 startPosition,
            Vector3 startDirection,
            float length,
            float startRadius,
            int depth,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles,
            List<BranchTip> branchTips,
            float inheritedWindWeight)
        {
            if (length < minimumBranchLength || startRadius < minimumBranchRadius)
            {
                BranchTip terminalTip = new BranchTip();
                terminalTip.position = startPosition + startDirection.normalized * Mathf.Max(0.1f, length);
                terminalTip.direction = startDirection.normalized;
                terminalTip.normal = GetStablePerpendicular(startDirection.normalized);
                terminalTip.size = Mathf.Clamp(length * 0.09f * terminalLeafSizeScale, 0.1f, 0.24f);
                terminalTip.density = hiddenBranchLeafDensity;
                terminalTip.spread = 0.36f;
                terminalTip.isTwig = true;
                terminalTip.isTerminal = true;
                terminalTip.isSurfaceAnchor = false;
                terminalTip.windWeight = Mathf.Clamp01(inheritedWindWeight + 0.12f);
                branchTips.Add(terminalTip);
                return;
            }

            int segmentCount = Mathf.Max(2, branchSegments - (depth - 1));
            float depthFactor = (float)(depth - 1) / Mathf.Max(1, maxBranchDepth - 1);

            float endRadius = Mathf.Max(
                minimumBranchRadius * 0.85f,
                startRadius * Mathf.Lerp(0.42f, 0.26f, depthFactor));

            float curvature = branchCurvature * Mathf.Lerp(1f, 0.65f, depthFactor);
            float upwardPull = Mathf.Lerp(branchUpwardBias, 0.9f, depthFactor * 0.8f);

            List<BranchPoint> path = BuildBranchPath(
                startPosition,
                startDirection.normalized,
                length,
                startRadius,
                endRadius,
                segmentCount,
                curvature,
                upwardPull);

            float branchWindBase = Mathf.Clamp01(inheritedWindWeight);
            float branchWindTip = Mathf.Clamp01(Mathf.Max(
                branchWindBase + Mathf.Lerp(0.3f, 0.42f, depthFactor),
                Mathf.Lerp(0.58f, 0.82f, depthFactor)));

            bool renderTube = depth <= maximumRenderableBranchDepth && startRadius >= minimumRenderableBranchRadius;

            bool spawnChildren = depth < maxBranchDepth
                && length > minimumBranchLength * Mathf.Lerp(1.25f, 1.55f, depthFactor)
                && startRadius > minimumBranchRadius * Mathf.Lerp(1.05f, 0.9f, depthFactor);

            int childCount = 0;

            if (spawnChildren)
            {
                if (depth == 1)
                {
                    childCount = RandomRangeInclusive(childBranchCountRange);
                }
                else
                {
                    int minimum = Mathf.Max(0, childBranchCountRange.x - (depth - 1));
                    int maximum = Mathf.Max(minimum, childBranchCountRange.y - (depth - 1));

                    if (depth >= maximumRenderableBranchDepth)
                    {
                        minimum = Mathf.Max(0, minimum - 1);
                        maximum = Mathf.Max(minimum, maximum - 1);
                    }

                    childCount = Random.Range(minimum, maximum + 1);
                }
            }

            bool capBranchEnd = (!spawnChildren || childCount == 0) && endRadius >= minimumCapRadius;

            if (renderTube)
            {
                AddTube(
                    path,
                    GetTubeRadialSegments(startRadius),
                    false,
                    capBranchEnd,
                    branchWindBase,
                    branchWindTip,
                    vertices,
                    normals,
                    uvs,
                    colors,
                    triangles);
            }

            if (renderTube && startRadius >= minimumLeafAnchorRadius)
            {
                float anchorDensityMultiplier = depth == 1
                    ? surfaceAnchorDensityScale * 1.15f
                    : surfaceAnchorDensityScale * 1.08f;

                AddLeafAnchorsAlongBranch(
                    path,
                    length,
                    depth,
                    branchTips,
                    anchorDensityMultiplier,
                    depth > 1,
                    false,
                    branchWindBase,
                    branchWindTip);
            }

            if (!renderTube)
            {
                AddLeafAnchorsAlongBranch(
                    path,
                    length,
                    depth,
                    branchTips,
                    hiddenBranchLeafDensity * 0.32f,
                    true,
                    true,
                    branchWindBase,
                    branchWindTip);
            }

            if (!renderTube || depth >= maximumRenderableBranchDepth)
            {
                AddHiddenBranchLeafTipsAlongPath(
                    path,
                    length,
                    branchTips,
                    branchWindBase,
                    branchWindTip);
            }

            if (renderTwigGeometry && renderTube && depth < maximumRenderableBranchDepth && startRadius >= minimumRenderableTwigRadius * 1.15f)
            {
                AddTwigBranchesAlongBranch(
                    path,
                    length,
                    depth,
                    vertices,
                    normals,
                    uvs,
                    colors,
                    triangles,
                    branchTips,
                    branchWindBase,
                    branchWindTip);
            }

            if (spawnChildren && childCount > 0)
            {
                float spiralOffset = Random.Range(0f, 360f);

                for (int i = 0; i < childCount; i++)
                {
                    float spawnStart = depth >= maximumRenderableBranchDepth ? 0.58f : 0.45f;
                    float spawnEnd = depth >= maximumRenderableBranchDepth ? 0.96f : 0.92f;
                    float spawnT = Mathf.Lerp(
                        spawnStart,
                        spawnEnd,
                        Mathf.Pow(Random.Range(0f, 1f), depth >= maximumRenderableBranchDepth ? 0.68f : 0.82f));

                    int pathIndex = Mathf.Clamp(
                        Mathf.RoundToInt(spawnT * (path.Count - 1)),
                        1,
                        path.Count - 2);

                    BranchPoint spawnPoint = path[pathIndex];
                    Vector3 tangent = spawnPoint.tangent;

                    float angle = spiralOffset + i * (360f / childCount) + Random.Range(-35f, 35f);
                    Vector3 outward = Quaternion.AngleAxis(angle, tangent) * GetStablePerpendicular(tangent);
                    outward = Vector3.ProjectOnPlane(outward, tangent).normalized;

                    if (outward.sqrMagnitude < 0.0001f)
                    {
                        outward = GetStablePerpendicular(tangent);
                    }

                    float hiddenChildScale = depth >= maximumRenderableBranchDepth ? 0.78f : 1f;

                    float childLength = length
                        * childBranchLengthScale
                        * Mathf.Lerp(1f, 0.58f, spawnT)
                        * Random.Range(0.88f, 1.08f)
                        * hiddenChildScale;

                    float childRadius = startRadius
                        * childBranchRadiusScale
                        * Mathf.Lerp(1f, 0.72f, spawnT)
                        * Random.Range(0.88f, 1.05f)
                        * Mathf.Lerp(1f, 0.78f, depth >= maximumRenderableBranchDepth ? 1f : 0f);

                    childRadius = Mathf.Max(minimumBranchRadius, childRadius);

                    Vector3 childDirection = Vector3.Slerp(
                        outward,
                        tangent,
                        Mathf.Lerp(0.42f, 0.72f, depthFactor + 0.2f)).normalized;

                    childDirection = Quaternion.AngleAxis(
                        Random.Range(-branchTwistJitter, branchTwistJitter),
                        tangent) * childDirection;

                    childDirection = Vector3.Slerp(
                        childDirection,
                        Vector3.up,
                        depth >= maximumRenderableBranchDepth ? 0.12f : 0.05f).normalized;

                    float childEmbed = Mathf.Min(spawnPoint.radius * 0.35f, childRadius * 1.25f);
                    Vector3 childStartPosition = spawnPoint.position + outward * (spawnPoint.radius - childEmbed);

                    float childInheritedWind = EvaluateWindAlongPath(branchWindBase, branchWindTip, spawnT);

                    GenerateBranchRecursive(
                        childStartPosition,
                        childDirection,
                        childLength,
                        childRadius,
                        depth + 1,
                        vertices,
                        normals,
                        uvs,
                        colors,
                        triangles,
                        branchTips,
                        childInheritedWind);
                }
            }

            BranchPoint endPoint = path[path.Count - 1];
            BranchTip endTip = new BranchTip();
            endTip.position = endPoint.position;
            endTip.direction = endPoint.tangent;
            endTip.normal = endPoint.normal;
            endTip.size = Mathf.Clamp(length * (renderTube ? 0.08f : 0.072f) * terminalLeafSizeScale, 0.09f, 0.28f);
            endTip.density = renderTube ? 0.9f * terminalLeafCardDensityScale : hiddenBranchLeafDensity;
            endTip.spread = renderTube ? 0.38f : 0.32f;
            endTip.isTwig = depth >= 3 || !renderTube;
            endTip.isTerminal = true;
            endTip.isSurfaceAnchor = false;
            endTip.windWeight = branchWindTip;
            branchTips.Add(endTip);
        }

        private List<BranchPoint> BuildBranchPath(
            Vector3 startPosition,
            Vector3 startDirection,
            float length,
            float startRadius,
            float endRadius,
            int segmentCount,
            float curvatureDegrees,
            float upwardPull)
        {
            List<BranchPoint> points = new List<BranchPoint>(segmentCount + 1);

            Vector3 position = startPosition;
            Vector3 direction = startDirection.normalized;
            Vector3 frameNormal = GetStablePerpendicular(direction);
            float accumulatedLength = 0f;
            float segmentLength = length / segmentCount;

            for (int i = 0; i <= segmentCount; i++)
            {
                float t = i / (float)segmentCount;
                float radius = Mathf.Lerp(startRadius, endRadius, Mathf.Pow(t, 0.85f));

                BranchPoint point = new BranchPoint();
                point.position = position;
                point.tangent = direction;
                point.normal = frameNormal;
                point.radius = radius;
                point.v = accumulatedLength;
                points.Add(point);

                if (i == segmentCount)
                {
                    continue;
                }

                Vector3 side = Vector3.Cross(direction, frameNormal).normalized;

                if (side.sqrMagnitude < 0.0001f)
                {
                    side = GetStablePerpendicular(direction);
                }

                Quaternion bend =
                    Quaternion.AngleAxis(Random.Range(-curvatureDegrees, curvatureDegrees), frameNormal) *
                    Quaternion.AngleAxis(Random.Range(-curvatureDegrees, curvatureDegrees), side);

                Vector3 nextDirection = (bend * direction).normalized;
                nextDirection = Vector3.Slerp(nextDirection, Vector3.up, 0.06f + upwardPull * 0.08f).normalized;

                Vector3 projectedNormal = Vector3.ProjectOnPlane(frameNormal, nextDirection);

                if (projectedNormal.sqrMagnitude < 0.0001f)
                {
                    projectedNormal = GetStablePerpendicular(nextDirection);
                }

                projectedNormal.Normalize();
                projectedNormal = (Quaternion.AngleAxis(Random.Range(-curvatureDegrees * 0.2f, curvatureDegrees * 0.2f), nextDirection) * projectedNormal).normalized;

                position += nextDirection * segmentLength;
                accumulatedLength += segmentLength;
                direction = nextDirection;
                frameNormal = projectedNormal;
            }

            return points;
        }

        private void AddTube(
            List<BranchPoint> path,
            int tubeSegments,
            bool capStart,
            bool capEnd,
            float windWeightStart,
            float windWeightEnd,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles)
        {
            if (path == null || path.Count < 2)
            {
                return;
            }

            tubeSegments = Mathf.Max(3, tubeSegments);

            int ringStartIndex = vertices.Count;
            int ringVertexCount = tubeSegments + 1;

            for (int i = 0; i < path.Count; i++)
            {
                BranchPoint point = path[i];
                Vector3 bitangent = Vector3.Cross(point.tangent, point.normal).normalized;

                if (bitangent.sqrMagnitude < 0.0001f)
                {
                    bitangent = GetStablePerpendicular(point.tangent);
                }

                float pathT = i / (float)(path.Count - 1);
                float windWeight = EvaluateWindAlongPath(windWeightStart, windWeightEnd, pathT);
                Color barkColor = new Color(1f, 1f, windWeight, 0f);

                for (int j = 0; j <= tubeSegments; j++)
                {
                    float angle01 = j / (float)tubeSegments;
                    float angleRadians = angle01 * Mathf.PI * 2f;

                    Vector3 radial = (point.normal * Mathf.Cos(angleRadians) + bitangent * Mathf.Sin(angleRadians)).normalized;
                    Vector3 vertex = point.position + radial * point.radius;

                    vertices.Add(vertex);
                    normals.Add(radial);
                    uvs.Add(new Vector2(angle01, point.v * barkUvScale));
                    colors.Add(barkColor);
                }
            }

            for (int ring = 0; ring < path.Count - 1; ring++)
            {
                int currentRing = ringStartIndex + ring * ringVertexCount;
                int nextRing = currentRing + ringVertexCount;

                for (int side = 0; side < tubeSegments; side++)
                {
                    int current = currentRing + side;
                    int currentNext = currentRing + side + 1;
                    int next = nextRing + side;
                    int nextNext = nextRing + side + 1;

                    triangles.Add(current);
                    triangles.Add(currentNext);
                    triangles.Add(nextNext);

                    triangles.Add(current);
                    triangles.Add(nextNext);
                    triangles.Add(next);
                }
            }

            if (capStart)
            {
                AddCap(
                    path[0],
                    tubeSegments,
                    -path[0].tangent.normalized,
                    false,
                    windWeightStart,
                    vertices,
                    normals,
                    uvs,
                    colors,
                    triangles);
            }

            if (capEnd)
            {
                AddCap(
                    path[path.Count - 1],
                    tubeSegments,
                    path[path.Count - 1].tangent.normalized,
                    true,
                    windWeightEnd,
                    vertices,
                    normals,
                    uvs,
                    colors,
                    triangles);
            }
        }

        private void AddCap(
            BranchPoint point,
            int capSegments,
            Vector3 normalDirection,
            bool forwardFacing,
            float windWeight,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles)
        {
            capSegments = Mathf.Max(3, capSegments);

            int centerIndex = vertices.Count;
            Vector3 bitangent = Vector3.Cross(point.tangent, point.normal).normalized;

            if (bitangent.sqrMagnitude < 0.0001f)
            {
                bitangent = GetStablePerpendicular(point.tangent);
            }

            Color barkColor = new Color(1f, 1f, windWeight, 0f);

            vertices.Add(point.position);
            normals.Add(normalDirection);
            uvs.Add(new Vector2(0.5f, 0.5f));
            colors.Add(barkColor);

            for (int i = 0; i < capSegments; i++)
            {
                float angle01 = i / (float)capSegments;
                float angleRadians = angle01 * Mathf.PI * 2f;

                Vector3 radial = (point.normal * Mathf.Cos(angleRadians) + bitangent * Mathf.Sin(angleRadians)).normalized;
                Vector3 vertex = point.position + radial * point.radius;

                vertices.Add(vertex);
                normals.Add(normalDirection);
                uvs.Add(new Vector2(radial.x * 0.5f + 0.5f, radial.y * 0.5f + 0.5f));
                colors.Add(barkColor);
            }

            for (int i = 0; i < capSegments; i++)
            {
                int current = centerIndex + 1 + i;
                int next = centerIndex + 1 + (i + 1) % capSegments;

                if (forwardFacing)
                {
                    triangles.Add(centerIndex);
                    triangles.Add(current);
                    triangles.Add(next);
                }
                else
                {
                    triangles.Add(centerIndex);
                    triangles.Add(next);
                    triangles.Add(current);
                }
            }
        }

        private void AddLeafClusters(
            List<BranchTip> branchTips,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles)
        {
            for (int i = 0; i < branchTips.Count; i++)
            {
                BranchTip tip = branchTips[i];

                float size01 = Mathf.Clamp01((tip.size - 0.08f) / 0.32f);
                float sizeFactor = Mathf.Lerp(0.95f, 1.22f, size01);

                if (tip.isTerminal)
                {
                    sizeFactor *= terminalLeafSizeScale;
                }

                int baseLeafCards = tip.isTwig ? twigLeafCards : branchLeafCards;

                float densityScale = leafCardDensity;

                if (tip.isSurfaceAnchor)
                {
                    densityScale *= surfaceAnchorDensityScale;
                }

                if (tip.isTwig)
                {
                    densityScale *= twigLeafDensity;
                }

                if (tip.isTerminal)
                {
                    densityScale *= terminalLeafDensity * terminalLeafCardDensityScale;
                }

                densityScale *= Mathf.Lerp(0.92f, 1.08f, size01);

                int minimumCards = tip.isSurfaceAnchor ? 1 : 2;
                int maximumCards;

                if (tip.isSurfaceAnchor)
                {
                    maximumCards = Mathf.Max(2, Mathf.RoundToInt(baseLeafCards * 1.35f));
                }
                else if (tip.isTerminal)
                {
                    maximumCards = Mathf.Max(minimumCards, Mathf.RoundToInt(baseLeafCards * 1.7f));
                }
                else
                {
                    maximumCards = Mathf.Max(minimumCards, Mathf.RoundToInt(baseLeafCards * 1.45f));
                }

                int cardCount = Mathf.Clamp(
                    Mathf.RoundToInt(
                        baseLeafCards *
                        tip.density *
                        densityScale *
                        Random.Range(0.97f, 1.05f)),
                    minimumCards,
                    maximumCards);

                if (tip.isSurfaceAnchor && size01 < 0.28f)
                {
                    cardCount = Mathf.Min(cardCount, 3);
                }

                float clusterRadius = leafClusterRadius
                    * Mathf.Lerp(0.36f, 0.76f, size01)
                    * Mathf.Lerp(0.9f, 1f, tip.spread);

                if (tip.isSurfaceAnchor)
                {
                    clusterRadius *= 0.72f;
                }

                if (tip.isTerminal)
                {
                    clusterRadius *= canopyClusterRadiusScale * 1.08f;
                }

                if (tip.isTwig)
                {
                    clusterRadius *= 0.92f;
                }

                float axialLength;

                if (tip.isTerminal)
                {
                    axialLength = clusterRadius * 0.95f * Mathf.Lerp(0.92f, 1.06f, tip.spread);
                }
                else if (tip.isSurfaceAnchor)
                {
                    axialLength = clusterRadius * 0.72f * Mathf.Lerp(0.9f, 1f, tip.spread);
                }
                else
                {
                    axialLength = clusterRadius * 1.35f * Mathf.Lerp(0.9f, 1.08f, tip.spread);
                }

                Vector3 branchForward = tip.direction.normalized;
                Vector3 branchNormal = Vector3.ProjectOnPlane(tip.normal, branchForward);

                if (branchNormal.sqrMagnitude < 0.0001f)
                {
                    branchNormal = GetStablePerpendicular(branchForward);
                }

                branchNormal.Normalize();

                Vector3 branchBinormal = Vector3.Cross(branchForward, branchNormal).normalized;

                if (branchBinormal.sqrMagnitude < 0.0001f)
                {
                    branchBinormal = Vector3.Cross(branchForward, GetStablePerpendicular(branchForward)).normalized;
                }

                float randomPhase = Random.Range(0f, 360f);
                float jitterScale = Mathf.Lerp(0.05f, 0.2f, leafPositionVariance);

                for (int j = 0; j < cardCount; j++)
                {
                    float t = (j + 0.5f) / cardCount;
                    float angle = (randomPhase + j * 137.507764f + Random.Range(-18f, 18f) * (0.45f + jitterScale)) * Mathf.Deg2Rad;

                    Vector3 radialDirection = (
                        branchNormal * Mathf.Cos(angle) +
                        branchBinormal * Mathf.Sin(angle)).normalized;

                    float radialT;

                    if (tip.isTerminal)
                    {
                        radialT = Mathf.Lerp(0.32f, 1f, Mathf.Pow(t, 0.72f));
                    }
                    else if (tip.isSurfaceAnchor)
                    {
                        radialT = Mathf.Lerp(0.12f, 0.92f, Mathf.Pow(t, 0.84f));
                    }
                    else
                    {
                        radialT = Mathf.Lerp(0.22f, 1f, Mathf.Pow(t, 0.8f));
                    }

                    float radialDistance = clusterRadius * radialT;
                    radialDistance += Random.Range(-clusterRadius * jitterScale * 0.12f, clusterRadius * jitterScale * 0.12f);

                    float axialBase;

                    if (tip.isTerminal)
                    {
                        axialBase = Mathf.Lerp(-0.36f, 0.28f, t);
                    }
                    else if (tip.isSurfaceAnchor)
                    {
                        axialBase = Mathf.Lerp(-0.08f, 0.34f, t);
                    }
                    else
                    {
                        axialBase = Mathf.Lerp(-0.22f, 0.52f, t);
                    }

                    axialBase += Random.Range(-jitterScale * 0.18f, jitterScale * 0.18f);

                    float verticalLift;

                    if (tip.isTerminal)
                    {
                        verticalLift = Mathf.Lerp(-0.02f, 0.08f, t);
                    }
                    else if (tip.isSurfaceAnchor)
                    {
                        verticalLift = Mathf.Lerp(-0.01f, 0.09f, t);
                    }
                    else
                    {
                        verticalLift = Mathf.Lerp(-0.02f, 0.11f, t);
                    }

                    verticalLift += Random.Range(-0.03f, 0.03f) * jitterScale;

                    Vector3 canopyUp = Vector3.Slerp(
                        radialDirection,
                        Vector3.up,
                        leafVerticalBias * (tip.isTerminal ? 0.18f : 0.1f)).normalized;

                    Vector3 rootPosition =
                        tip.position +
                        branchForward * (axialBase * axialLength) +
                        radialDirection * radialDistance +
                        canopyUp * (clusterRadius * verticalLift);

                    if (tip.isTerminal)
                    {
                        rootPosition -= radialDirection * (clusterRadius * 0.08f);
                    }

                    float pushIn = leafRootPushIn * Mathf.Lerp(0.85f, 1.15f, size01);
                    float pushBack = leafRootBackOffset * Mathf.Lerp(0.85f, 1.1f, size01);

                    if (tip.isTwig)
                    {
                        pushIn *= 0.88f;
                        pushBack *= 0.82f;
                    }

                    if (tip.isSurfaceAnchor)
                    {
                        rootPosition -= radialDirection * pushIn;
                        rootPosition -= branchForward * pushBack;
                    }
                    else
                    {
                        rootPosition -= radialDirection * (pushIn * 0.18f);
                    }

                    rootPosition += branchBinormal * Random.Range(-clusterRadius * jitterScale * 0.1f, clusterRadius * jitterScale * 0.1f);
                    rootPosition += branchNormal * Random.Range(-clusterRadius * jitterScale * 0.1f, clusterRadius * jitterScale * 0.1f);

                    float leafScale = Random.Range(0.94f, 1.08f);

                    if (tip.isTwig)
                    {
                        leafScale *= 0.93f;
                    }

                    float width = Random.Range(leafWidthRange.x, leafWidthRange.y) * sizeFactor * leafScale;
                    float height = Random.Range(leafHeightRange.x, leafHeightRange.y) * sizeFactor * leafScale;

                    Quaternion baseRotation = CreateLeafRotation(
                        branchForward,
                        branchNormal,
                        radialDirection,
                        canopyUp,
                        tip.isTerminal);

                    Quaternion rotation =
                        baseRotation *
                        Quaternion.Euler(
                            Random.Range(-leafTiltJitter, leafTiltJitter) * (0.24f + leafPositionVariance * 0.24f),
                            Random.Range(-leafYawVariance, leafYawVariance) * (0.14f + leafPositionVariance * 0.18f),
                            Random.Range(-leafRollVariance, leafRollVariance) * (0.18f + leafPositionVariance * 0.22f));

                    float variation = Random.Range(0f, 1f);

                    if (useSingleSidedLeafGeometry)
                    {
                        AddLeafQuad(rootPosition, rotation, width, height, variation, tip.windWeight, vertices, normals, uvs, colors, triangles);
                    }
                    else
                    {
                        AddLeafQuadDoubleSided(rootPosition, rotation, width, height, variation, tip.windWeight, vertices, normals, uvs, colors, triangles);
                    }
                }
            }
        }

        private void AddLeafQuadDoubleSided(
            Vector3 center,
            Quaternion rotation,
            float width,
            float height,
            float variation,
            float inheritedWindWeight,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles)
        {
            Vector3 right = rotation * Vector3.right * (width * 0.5f);
            Vector3 up = rotation * Vector3.up * (height * 0.5f);
            Vector3 normal = Vector3.Normalize(Vector3.Cross(right, up));
            Color leafColor = new Color(variation, variation, inheritedWindWeight, 1f);

            int index = vertices.Count;

            vertices.Add(center - right);
            vertices.Add(center - right + up * 2f);
            vertices.Add(center + right + up * 2f);
            vertices.Add(center + right);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(1f, 0f));

            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);

            triangles.Add(index + 0);
            triangles.Add(index + 1);
            triangles.Add(index + 2);
            triangles.Add(index + 0);
            triangles.Add(index + 2);
            triangles.Add(index + 3);

            int backIndex = vertices.Count;

            vertices.Add(center - right);
            vertices.Add(center + right);
            vertices.Add(center + right + up * 2f);
            vertices.Add(center - right + up * 2f);

            normals.Add(-normal);
            normals.Add(-normal);
            normals.Add(-normal);
            normals.Add(-normal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);

            triangles.Add(backIndex + 0);
            triangles.Add(backIndex + 1);
            triangles.Add(backIndex + 2);
            triangles.Add(backIndex + 0);
            triangles.Add(backIndex + 2);
            triangles.Add(backIndex + 3);
        }

        private void AddLeafAnchorsAlongBranch(
           List<BranchPoint> path,
           float length,
           int depth,
           List<BranchTip> branchTips,
           float densityMultiplier,
           bool biasToOuterHalf,
           bool isTwigSource,
           float windWeightStart,
           float windWeightEnd)
        {
            if (path == null || path.Count < 2)
            {
                return;
            }

            float depthFactor = (float)(depth - 1) / Mathf.Max(1, maxBranchDepth - 1);
            float startT = Mathf.Clamp01(leafStartAlongBranch);
            float endT = isTwigSource ? 0.9f : (depth == 1 ? 0.92f : 0.9f);

            if (startT >= endT)
            {
                startT = Mathf.Max(0f, endT - 0.04f);
            }

            float averageRadius = GetAveragePathRadius(path);
            float radiusReference = isTwigSource
                ? minimumRenderableTwigRadius * 3f
                : minimumRenderableBranchRadius * 3.5f;

            float radiusFactor = Mathf.Clamp01(
                averageRadius / Mathf.Max(0.001f, radiusReference));

            float baseAnchorCount =
                Mathf.Lerp(4f, 8f, 1f - depthFactor) *
                Mathf.Clamp(length * 0.52f, 0.75f, 1.25f) *
                densityMultiplier *
                leafAnchorDensity;

            if (isTwigSource)
            {
                baseAnchorCount *= twigAnchorDensityScale;
            }

            baseAnchorCount *= Mathf.Lerp(isTwigSource ? 0.25f : 0.45f, 1f, radiusFactor);

            int minimumAnchors = isTwigSource ? 1 : 2;
            int maximumAnchors = isTwigSource ? 4 : 10;

            int anchorCount = Mathf.Clamp(
                Mathf.RoundToInt(baseAnchorCount),
                minimumAnchors,
                maximumAnchors);

            if (averageRadius < minimumLeafAnchorRadius * 0.85f)
            {
                anchorCount = Mathf.Min(anchorCount, isTwigSource ? 1 : 2);
            }

            float spiralOffset = Random.Range(0f, 360f);
            int maximumPathIndex = Mathf.Max(1, path.Count - 2);

            for (int i = 0; i < anchorCount; i++)
            {
                float anchorLerp = anchorCount == 1 ? 1f : i / (float)(anchorCount - 1);
                float distributedT = biasToOuterHalf ? Mathf.Pow(anchorLerp, 0.72f) : Mathf.Pow(anchorLerp, 0.82f);
                float pathT = Mathf.Lerp(startT, endT, distributedT);

                if (anchorCount > 2)
                {
                    pathT += Random.Range(-0.035f, 0.035f) * Mathf.Lerp(0.35f, 1f, leafPositionVariance);
                    pathT = Mathf.Clamp(pathT, startT, endT);
                }

                int pathIndex = Mathf.Clamp(
                    Mathf.RoundToInt(pathT * (path.Count - 1)),
                    1,
                    maximumPathIndex);

                BranchPoint point = path[pathIndex];
                float angle = spiralOffset + i * 137.5f + Random.Range(-18f, 18f);

                Vector3 anchorNormal = Quaternion.AngleAxis(angle, point.tangent) * point.normal;
                anchorNormal = Vector3.ProjectOnPlane(anchorNormal, point.tangent).normalized;

                if (anchorNormal.sqrMagnitude < 0.0001f)
                {
                    anchorNormal = point.normal;
                }

                float surfaceOffset = isTwigSource
                    ? Mathf.Max(0.012f, point.radius * 0.72f)
                    : Mathf.Max(0.014f, point.radius * 0.82f);

                BranchTip anchor = new BranchTip();
                anchor.position =
                    point.position +
                    anchorNormal * surfaceOffset +
                    point.tangent * Random.Range(-point.radius * 0.08f, point.radius * 0.12f);
                anchor.direction = point.tangent;
                anchor.normal = anchorNormal;
                anchor.size = Mathf.Clamp(
                    length *
                    Mathf.Lerp(0.045f, 0.082f, Mathf.Pow(pathT, 0.8f)) *
                    Mathf.Lerp(1f, 0.84f, depthFactor) *
                    (isTwigSource ? 0.94f : 1f),
                    0.075f,
                    isTwigSource ? 0.26f : 0.34f);
                anchor.density = Mathf.Lerp(
                    isTwigSource ? 1.15f : 1.08f,
                    isTwigSource ? 0.82f : 0.9f,
                    pathT) * densityMultiplier;
                anchor.spread = Mathf.Lerp(0.82f, 1f, pathT) * (isTwigSource ? 0.84f : 0.92f);
                anchor.isTwig = isTwigSource;
                anchor.isTerminal = false;
                anchor.isSurfaceAnchor = true;
                anchor.windWeight = EvaluateWindAlongPath(windWeightStart, windWeightEnd, pathT);

                if (isTwigSource)
                {
                    anchor.windWeight = Mathf.Clamp01(anchor.windWeight + 0.05f);
                }

                branchTips.Add(anchor);
            }
        }

        private void AddTwigBranchesAlongBranch(
            List<BranchPoint> path,
            float length,
            int depth,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles,
            List<BranchTip> branchTips,
            float parentWindStart,
            float parentWindEnd)
        {
            if (path == null || path.Count < 3)
            {
                return;
            }

            if (depth > 2)
            {
                return;
            }

            float averagePathRadius = GetAveragePathRadius(path);
            float twigSourceFactor = Mathf.Clamp01(
                averagePathRadius / Mathf.Max(0.001f, minimumRenderableTwigRadius * 2.5f));

            int twigCount = Mathf.RoundToInt(
                (RandomRangeInclusive(twigBranchCountRange) + (depth == 1 ? 2 : 1))
                * Mathf.Lerp(0.35f, 1f, twigSourceFactor));

            if (twigCount <= 0)
            {
                return;
            }

            float startT = depth == 1 ? Mathf.Max(leafStartAlongBranch, 0.38f) : Mathf.Max(leafStartAlongBranch + 0.04f, 0.3f);
            float endT = 0.88f;
            int maximumPathIndex = Mathf.Max(1, path.Count - 2);

            for (int i = 0; i < twigCount; i++)
            {
                float spawnT = Mathf.Lerp(startT, endT, Mathf.Pow(Random.Range(0f, 1f), 0.58f));
                int pathIndex = Mathf.Clamp(
                    Mathf.RoundToInt(spawnT * (path.Count - 1)),
                    1,
                    maximumPathIndex);

                BranchPoint spawnPoint = path[pathIndex];
                Vector3 tangent = spawnPoint.tangent;

                Vector3 outward = Quaternion.AngleAxis(Random.Range(-160f, 160f), tangent) * spawnPoint.normal;
                outward = Vector3.ProjectOnPlane(outward, tangent).normalized;

                if (outward.sqrMagnitude < 0.0001f)
                {
                    outward = GetStablePerpendicular(tangent);
                }

                float twigLengthMin = Mathf.Lerp(twigLengthRange.x, twigLengthRange.y, 0.35f);
                float twigLengthMax = Mathf.Min(0.5f, twigLengthRange.y * 1.15f);
                twigLengthMax = Mathf.Max(twigLengthMin, twigLengthMax);

                float twigLength = length
                    * Random.Range(twigLengthMin, twigLengthMax)
                    * Mathf.Lerp(1f, 0.72f, spawnT)
                    * (depth == 1 ? 1.08f : 0.84f);

                float twigRadius = Mathf.Max(
                    minimumBranchRadius * 0.6f,
                    spawnPoint.radius * twigRadiusScale * Random.Range(0.8f, 1.05f));

                if (twigLength < minimumBranchLength * 0.35f)
                {
                    continue;
                }

                Vector3 twigDirection = Vector3.Slerp(
                    outward,
                    tangent,
                    Random.Range(0.08f, 0.22f)).normalized;

                twigDirection = Vector3.Slerp(
                    twigDirection,
                    Vector3.up,
                    0.16f + branchUpwardBias * 0.14f).normalized;

                twigDirection = Quaternion.AngleAxis(
                    Random.Range(-twigSpread, twigSpread),
                    tangent) * twigDirection;

                float twigEmbed = Mathf.Min(spawnPoint.radius * 0.28f, twigRadius * 0.95f);
                Vector3 twigStartPosition = spawnPoint.position + outward * (spawnPoint.radius - twigEmbed);

                int twigSegmentCount = Mathf.Max(2, twigSegments - (depth - 1));
                float twigEndRadius = Mathf.Max(minimumBranchRadius * 0.3f, twigRadius * 0.12f);

                float twigBaseWind = EvaluateWindAlongPath(parentWindStart, parentWindEnd, spawnT);
                float twigTipWind = Mathf.Clamp01(Mathf.Max(twigBaseWind + 0.26f, 0.88f));

                List<BranchPoint> twigPath = BuildBranchPath(
                    twigStartPosition,
                    twigDirection.normalized,
                    twigLength,
                    twigRadius,
                    twigEndRadius,
                    twigSegmentCount,
                    branchCurvature * 0.42f,
                    Mathf.Lerp(0.62f, 0.9f, spawnT));

                bool renderTwigTube = twigRadius >= minimumRenderableTwigRadius;
                bool addTwigAnchors = twigRadius >= minimumLeafAnchorRadius;
                bool capTwigEnd = twigEndRadius >= minimumCapRadius;

                if (renderTwigTube)
                {
                    AddTube(
                        twigPath,
                        GetTubeRadialSegments(twigRadius),
                        false,
                        capTwigEnd,
                        twigBaseWind,
                        twigTipWind,
                        vertices,
                        normals,
                        uvs,
                        colors,
                        triangles);
                }

                if (addTwigAnchors)
                {
                    AddLeafAnchorsAlongBranch(
                        twigPath,
                        twigLength,
                        depth + 1,
                        branchTips,
                        1.1f,
                        true,
                        true,
                        twigBaseWind,
                        twigTipWind);
                }

                BranchPoint twigEndPoint = twigPath[twigPath.Count - 1];
                BranchTip twigTip = new BranchTip();
                twigTip.position = twigEndPoint.position;
                twigTip.direction = twigEndPoint.tangent;
                twigTip.normal = twigEndPoint.normal;
                twigTip.size = Mathf.Clamp(twigLength * 0.08f, 0.08f, 0.18f);
                twigTip.density = renderTwigTube ? 0.72f : 0.5f;
                twigTip.spread = renderTwigTube ? 0.34f : 0.28f;
                twigTip.isTwig = true;
                twigTip.isTerminal = true;
                twigTip.isSurfaceAnchor = false;
                twigTip.windWeight = twigTipWind;
                branchTips.Add(twigTip);
            }
        }

        private void AddLeafQuad(
            Vector3 center,
            Quaternion rotation,
            float width,
            float height,
            float variation,
            float inheritedWindWeight,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color> colors,
            List<int> triangles)
        {
            Vector3 right = rotation * Vector3.right * (width * 0.5f);
            Vector3 up = rotation * Vector3.up * (height * 0.5f);
            Vector3 normal = Vector3.Normalize(Vector3.Cross(right, up));
            Color leafColor = new Color(variation, variation, inheritedWindWeight, 1f);

            int index = vertices.Count;

            vertices.Add(center - right);
            vertices.Add(center - right + up * 2f);
            vertices.Add(center + right + up * 2f);
            vertices.Add(center + right);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(1f, 0f));

            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);
            colors.Add(leafColor);

            triangles.Add(index + 0);
            triangles.Add(index + 1);
            triangles.Add(index + 2);
            triangles.Add(index + 0);
            triangles.Add(index + 2);
            triangles.Add(index + 3);
        }

        private void AddHiddenBranchLeafTipsAlongPath(
            List<BranchPoint> path,
            float length,
            List<BranchTip> branchTips,
            float windWeightStart,
            float windWeightEnd)
        {
            if (path == null || path.Count < 2)
            {
                return;
            }

            int tipCount = Mathf.Clamp(
                Mathf.RoundToInt(hiddenBranchLeafTipCount * Mathf.Lerp(0.85f, 1.2f, Mathf.Clamp01(length))),
                1,
                8);

            for (int i = 0; i < tipCount; i++)
            {
                float pathT = Mathf.Lerp(0.42f, 0.96f, (i + Random.Range(0.15f, 0.85f)) / tipCount);
                int pathIndex = Mathf.Clamp(
                    Mathf.RoundToInt(pathT * (path.Count - 1)),
                    1,
                    path.Count - 1);

                BranchPoint point = path[pathIndex];
                Vector3 outward = Quaternion.AngleAxis(Random.Range(-35f, 35f), point.tangent) * point.normal;
                outward = Vector3.ProjectOnPlane(outward, point.tangent).normalized;

                if (outward.sqrMagnitude < 0.0001f)
                {
                    outward = point.normal;
                }

                BranchTip tip = new BranchTip();
                tip.position = point.position + outward * Mathf.Max(point.radius * 0.6f, 0.03f);
                tip.direction = point.tangent;
                tip.normal = outward;
                tip.size = Mathf.Clamp(length * 0.09f * terminalLeafSizeScale, 0.1f, 0.28f);
                tip.density = hiddenBranchLeafDensity * Random.Range(0.9f, 1.1f);
                tip.spread = 0.42f;
                tip.isTwig = true;
                tip.isTerminal = true;
                tip.isSurfaceAnchor = false;
                tip.windWeight = EvaluateWindAlongPath(windWeightStart, windWeightEnd, pathT);
                branchTips.Add(tip);
            }
        }

        private int GetTubeRadialSegments(float startRadius)
        {
            int minimumSegments = 3;
            int maximumSegments = Mathf.Clamp(radialSegments, minimumSegments, 8);

            if (maximumSegments <= minimumSegments)
            {
                return minimumSegments;
            }

            float normalizedRadius = trunkRadius > 0.0001f
                ? Mathf.Clamp01(startRadius / trunkRadius)
                : 1f;

            int segmentCount = Mathf.RoundToInt(
                Mathf.Lerp(minimumSegments, maximumSegments, Mathf.Pow(normalizedRadius, 0.65f)));

            if (normalizedRadius <= 0.12f)
            {
                segmentCount = Mathf.Min(segmentCount, 4);
            }
            else if (normalizedRadius <= 0.26f)
            {
                segmentCount = Mathf.Min(segmentCount, 5);
            }
            else if (normalizedRadius <= 0.5f)
            {
                segmentCount = Mathf.Min(segmentCount, 6);
            }

            return Mathf.Clamp(segmentCount, minimumSegments, maximumSegments);
        }

        private float GetAveragePathRadius(List<BranchPoint> path)
        {
            if (path == null || path.Count == 0)
            {
                return 0f;
            }

            float totalRadius = 0f;

            for (int i = 0; i < path.Count; i++)
            {
                totalRadius += path[i].radius;
            }

            return totalRadius / path.Count;
        }

        private int RandomRangeInclusive(Vector2Int range)
        {
            return Random.Range(range.x, range.y + 1);
        }

        private Vector3 GetStablePerpendicular(Vector3 direction)
        {
            Vector3 axis = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.92f ? Vector3.forward : Vector3.up;
            Vector3 perpendicular = Vector3.Cross(direction, axis);

            if (perpendicular.sqrMagnitude < 0.0001f)
            {
                axis = Vector3.right;
                perpendicular = Vector3.Cross(direction, axis);
            }

            return perpendicular.normalized;
        }

        private void ReleaseMesh(ref Mesh mesh, MeshFilter targetMeshFilter)
        {
            if (mesh == null)
            {
                return;
            }

            if (targetMeshFilter != null && targetMeshFilter.sharedMesh == mesh)
            {
                targetMeshFilter.sharedMesh = null;
            }

            if (Application.isPlaying)
            {
                Destroy(mesh);
            }
            else
            {
                DestroyImmediate(mesh);
            }

            mesh = null;
        }

        private float EvaluateWindAlongPath(float startWeight, float endWeight, float t)
        {
            float normalizedT = Mathf.Pow(Mathf.Clamp01(t), 0.8f);
            return Mathf.Lerp(startWeight, endWeight, normalizedT);
        }

        private Quaternion CreateLeafRotation(
            Vector3 branchForward,
            Vector3 branchNormal,
            Vector3 radialDirection,
            Vector3 canopyUp,
            bool isTerminal)
        {
            Vector3 cardUp = Vector3.Slerp(canopyUp, branchForward, leafDirectionAlignment);

            if (isTerminal)
            {
                cardUp = Vector3.Slerp(cardUp, canopyUp, 0.12f);
            }

            if (cardUp.sqrMagnitude < 0.0001f)
            {
                cardUp = branchForward;
            }

            cardUp.Normalize();

            Vector3 surfaceNormal = Vector3.Slerp(radialDirection, branchNormal, leafNormalAlignment);
            Vector3 faceDirection = Vector3.ProjectOnPlane(surfaceNormal, cardUp);

            if (faceDirection.sqrMagnitude < 0.0001f)
            {
                faceDirection = Vector3.ProjectOnPlane(GetStablePerpendicular(cardUp), cardUp);
            }

            faceDirection.Normalize();

            return Quaternion.LookRotation(faceDirection, cardUp);
        }

        private void GenerateTreeMesh(ref Mesh targetMesh, string meshName, float lodFactor)
        {
            if (targetMesh == null)
            {
                targetMesh = new Mesh();
                targetMesh.name = meshName;
            }
            else
            {
                targetMesh.Clear();
                targetMesh.name = meshName;
            }

            GenerationSettingsSnapshot snapshot;
            ApplyLodGenerationScale(lodFactor, out snapshot);

            Random.State previousState = Random.state;

            try
            {
                Random.InitState(seed);

                List<Vector3> vertices = new List<Vector3>(8192);
                List<Vector3> normals = new List<Vector3>(8192);
                List<Vector2> uvs = new List<Vector2>(8192);
                List<Color> colors = new List<Color>(8192);
                List<int> triangles = new List<int>(16384);
                List<BranchTip> branchTips = new List<BranchTip>(256);

                float trunkLength = treeHeight * 0.92f;
                float trunkStartRadius = trunkRadius * 1.15f;
                float trunkEndRadius = Mathf.Max(minimumBranchRadius, trunkRadius * 0.18f);

                Vector3 trunkDirection = Quaternion.Euler(
                    Random.Range(-trunkLean, trunkLean),
                    0f,
                    Random.Range(-trunkLean, trunkLean)) * Vector3.up;

                List<BranchPoint> trunkPath = BuildBranchPath(
                    Vector3.zero,
                    trunkDirection.normalized,
                    trunkLength,
                    trunkStartRadius,
                    trunkEndRadius,
                    trunkSegments,
                    trunkCurvature,
                    0.85f);

                AddTube(trunkPath, GetTubeRadialSegments(trunkStartRadius), true, false, 0f, 0f, vertices, normals, uvs, colors, triangles);
                GenerateBranchesFromTrunk(trunkPath, vertices, normals, uvs, colors, triangles, branchTips);

                Random.InitState(seed ^ (leafSeed * 486187739));
                AddLeafClusters(branchTips, vertices, normals, uvs, colors, triangles);

                targetMesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                targetMesh.SetVertices(vertices);
                targetMesh.SetNormals(normals);
                targetMesh.SetUVs(0, uvs);
                targetMesh.SetColors(colors);
                targetMesh.SetTriangles(triangles, 0);
                targetMesh.RecalculateBounds();
            }
            finally
            {
                Random.state = previousState;
                RestoreGenerationSettings(snapshot);
            }
        }

        private void CacheLodChildComponents()
        {
            if (lod1Object != null)
            {
                lod1MeshFilter = lod1Object.GetComponent<MeshFilter>();
                lod1MeshRenderer = lod1Object.GetComponent<MeshRenderer>();
            }
            else
            {
                lod1MeshFilter = null;
                lod1MeshRenderer = null;
            }

            if (lod2Object != null)
            {
                lod2MeshFilter = lod2Object.GetComponent<MeshFilter>();
                lod2MeshRenderer = lod2Object.GetComponent<MeshRenderer>();
            }
            else
            {
                lod2MeshFilter = null;
                lod2MeshRenderer = null;
            }
        }

        private void EnsureLodObjects()
        {
            if (lodGroup == null)
            {
                lodGroup = gameObject.GetComponent<LODGroup>();

                if (lodGroup == null)
                {
                    lodGroup = gameObject.AddComponent<LODGroup>();
                }
            }

            if (lod1Object == null)
            {
                lod1Object = new GameObject("LOD1", typeof(MeshFilter), typeof(MeshRenderer));
                lod1Object.transform.SetParent(transform, false);
                lod1Object.layer = gameObject.layer;
            }

            if (lod2Object == null)
            {
                lod2Object = new GameObject("LOD2", typeof(MeshFilter), typeof(MeshRenderer));
                lod2Object.transform.SetParent(transform, false);
                lod2Object.layer = gameObject.layer;
            }

            CacheLodChildComponents();
        }

        private void CleanupLodObjects(bool removeLodGroup)
        {
            if (lod1Object != null)
            {
                ReleaseMesh(ref lod1Mesh, lod1MeshFilter);

                if (Application.isPlaying)
                {
                    Destroy(lod1Object);
                }
                else
                {
                    DestroyImmediate(lod1Object);
                }

                lod1Object = null;
            }
            else
            {
                ReleaseMesh(ref lod1Mesh, lod1MeshFilter);
            }

            if (lod2Object != null)
            {
                ReleaseMesh(ref lod2Mesh, lod2MeshFilter);

                if (Application.isPlaying)
                {
                    Destroy(lod2Object);
                }
                else
                {
                    DestroyImmediate(lod2Object);
                }

                lod2Object = null;
            }
            else
            {
                ReleaseMesh(ref lod2Mesh, lod2MeshFilter);
            }

            lod1MeshFilter = null;
            lod1MeshRenderer = null;
            lod2MeshFilter = null;
            lod2MeshRenderer = null;

            if (removeLodGroup && lodGroup != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(lodGroup);
                }
                else
                {
                    DestroyImmediate(lodGroup);
                }

                lodGroup = null;
            }
        }

        private void ConfigureLodGroup(Vector3 boundsSize)
        {
            if (lodGroup == null || lod1MeshRenderer == null || lod2MeshRenderer == null)
            {
                return;
            }

            float lod0Height = GetScreenRelativeTransitionHeight(boundsSize, lod1Distance);
            float lod1Height = GetScreenRelativeTransitionHeight(boundsSize, lod2Distance);
            float lod2Height = GetScreenRelativeTransitionHeight(boundsSize, lodCullDistance);

            lod0Height = Mathf.Clamp(lod0Height, 0.02f, 1f);
            lod1Height = Mathf.Clamp(Mathf.Min(lod1Height, lod0Height - 0.01f), 0.01f, lod0Height - 0.01f);
            lod2Height = Mathf.Clamp(Mathf.Min(lod2Height, lod1Height - 0.01f), 0.001f, lod1Height - 0.01f);

            LOD[] lods = new LOD[3];
            lods[0] = new LOD(lod0Height, new Renderer[] { meshRenderer });
            lods[1] = new LOD(lod1Height, new Renderer[] { lod1MeshRenderer });
            lods[2] = new LOD(lod2Height, new Renderer[] { lod2MeshRenderer });

            lodGroup.animateCrossFading = false;
            lodGroup.fadeMode = LODFadeMode.None;
            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            lodGroup.enabled = true;
        }

        private float GetScreenRelativeTransitionHeight(Vector3 boundsSize, float distance)
        {
            float objectSize = Mathf.Max(boundsSize.x, boundsSize.y, boundsSize.z);
            float halfFovRadians = lodReferenceFieldOfView * 0.5f * Mathf.Deg2Rad;
            float denominator = 2f * Mathf.Max(0.01f, distance) * Mathf.Tan(halfFovRadians);

            if (denominator <= 0f)
            {
                return 1f;
            }

            return objectSize / denominator;
        }

        private void SyncLodRendererSettings(MeshRenderer targetRenderer)
        {
            if (meshRenderer == null || targetRenderer == null)
            {
                return;
            }

            targetRenderer.sharedMaterials = meshRenderer.sharedMaterials;
            targetRenderer.shadowCastingMode = meshRenderer.shadowCastingMode;
            targetRenderer.receiveShadows = meshRenderer.receiveShadows;
            targetRenderer.motionVectorGenerationMode = meshRenderer.motionVectorGenerationMode;
            targetRenderer.lightProbeUsage = meshRenderer.lightProbeUsage;
            targetRenderer.reflectionProbeUsage = meshRenderer.reflectionProbeUsage;
            targetRenderer.allowOcclusionWhenDynamic = meshRenderer.allowOcclusionWhenDynamic;
            targetRenderer.probeAnchor = meshRenderer.probeAnchor;
            targetRenderer.enabled = true;
        }

        private void ApplyLodGenerationScale(float lodFactor, out GenerationSettingsSnapshot snapshot)
        {
            snapshot = new GenerationSettingsSnapshot
            {
                radialSegments = radialSegments,
                trunkSegments = trunkSegments,
                branchSegments = branchSegments,
                twigSegments = twigSegments,
                maxBranchDepth = maxBranchDepth,
                mainBranchCountRange = mainBranchCountRange,
                childBranchCountRange = childBranchCountRange,
                twigBranchCountRange = twigBranchCountRange,
                branchLeafCards = branchLeafCards,
                twigLeafCards = twigLeafCards
            };

            if (lodFactor >= 0.999f)
            {
                return;
            }

            float clampedFactor = Mathf.Clamp(lodFactor, 0.1f, 1f);

            float radialScale;

            if (clampedFactor >= 0.5f)
            {
                radialScale = 0.65f;
            }
            else
            {
                radialScale = 0.4f;
            }

            radialSegments = Mathf.Max(3, Mathf.RoundToInt(snapshot.radialSegments * radialScale));

            branchLeafCards = Mathf.Max(1, Mathf.RoundToInt(snapshot.branchLeafCards * clampedFactor));
            twigLeafCards = Mathf.Max(1, Mathf.RoundToInt(snapshot.twigLeafCards * clampedFactor));
        }

        private void RestoreGenerationSettings(GenerationSettingsSnapshot snapshot)
        {
            radialSegments = snapshot.radialSegments;
            trunkSegments = snapshot.trunkSegments;
            branchSegments = snapshot.branchSegments;
            twigSegments = snapshot.twigSegments;
            maxBranchDepth = snapshot.maxBranchDepth;
            mainBranchCountRange = snapshot.mainBranchCountRange;
            childBranchCountRange = snapshot.childBranchCountRange;
            twigBranchCountRange = snapshot.twigBranchCountRange;
            branchLeafCards = snapshot.branchLeafCards;
            twigLeafCards = snapshot.twigLeafCards;
        }
        #endregion
    }
}