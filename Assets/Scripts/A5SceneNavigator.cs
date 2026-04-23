using System.Collections;
using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Student template for A5.
/// Students should focus on the three public tool methods below:
/// 1. GetSceneCandidates()
/// 2. MoveToTarget(string targetId)
/// 3. BuildVoxelObject(string targetId, string blocksJson = null)
///
/// The helper methods are provided so students can focus on the agent-facing logic
/// instead of low-level MRUK and Unity details.
/// </summary>
[DisallowMultipleComponent]
public class A5SceneNavigator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject movingObject;

    [Header("Scene Candidates")]
    [SerializeField]
    private MRUKAnchor.SceneLabels candidateLabels =
        MRUKAnchor.SceneLabels.TABLE |
        MRUKAnchor.SceneLabels.COUCH |
        MRUKAnchor.SceneLabels.STORAGE |
        MRUKAnchor.SceneLabels.BED |
        MRUKAnchor.SceneLabels.SCREEN |
        MRUKAnchor.SceneLabels.LAMP |
        MRUKAnchor.SceneLabels.PLANT |
        MRUKAnchor.SceneLabels.WALL_ART;

    [Header("Placement")]
    [SerializeField, Min(0f)] private float surfaceOffset = 0.01f;

    [Header("Animation")]
    [SerializeField, Min(0.01f)] private float moveSpeed = 0.75f;

    [Header("Voxel Build")]
    [SerializeField, Min(0.01f)] private float voxelCubeSize = 0.06f;
    [SerializeField, Min(1)] private int maxVoxelBlocks = 24;
    [SerializeField] private Material voxelMaterial;
    [SerializeField] private Transform voxelObjectsRoot;

    private Coroutine moveCoroutine;
    private int voxelBuildCounter;

    [Serializable]
    private class SceneCandidatesPayload
    {
        public string status;
        public string message;
        public SceneCandidateInfo[] candidates;
    }

    [Serializable]
    private class SceneCandidateInfo
    {
        public string id;
        public string label;
        public string name;
        public int indexWithinLabel;
        public float distanceMeters;
        public Vector3 worldCenter;
    }

    [Serializable]
    private class MoveTargetPayload
    {
        public string status;
        public string message;
        public string targetId;
        public string label;
        public string name;
    }

    [Serializable]
    private class BuildVoxelPayload
    {
        public string status;
        public string message;
        public string targetId;
        public string label;
        public string name;
        public string objectName;
        public int builtBlockCount;
    }

    [Serializable]
    private class VoxelBlocksInput
    {
        public VoxelBlockSpec[] blocks;
    }

    [Serializable]
    private class VoxelBlockSpec
    {
        public int x;
        public int y;
        public int z;
    }

    private struct SceneCandidate
    {
        public MRUKAnchor Anchor;
        public SceneCandidateInfo Info;
    }

    public string GetSceneCandidates()
    {
        // TODO:
        // 1. Create a SceneCandidatesPayload.
        var payload = new SceneCandidatesPayload();
        // 2. Use TryGetCurrentRoom(...) to get the current MRUK room.
        if (!TryGetCurrentRoom(out var room, out var error))
        {
            payload.status = "error";
            payload.message = error;
            payload.candidates = Array.Empty<SceneCandidateInfo>();
            return JsonUtility.ToJson(payload);
        }

        // 3. Use BuildSceneCandidates(...) to collect scene targets.
        var candidates = BuildSceneCandidates(room);

        // 4. Copy each candidate's Info into payload.candidates.
        payload.status = "ok";
        payload.message = $"Found {candidates.Count} scene candidate(s).";
        payload.candidates = new SceneCandidateInfo[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            payload.candidates[i] = candidates[i].Info;
        }

        // 5. Return JsonUtility.ToJson(payload).
        return JsonUtility.ToJson(payload);
    }

    public string MoveToTarget(string targetId)
    {
        // TODO:
        // 1. Create a MoveTargetPayload.
        // 2. Use TryResolveTarget(...) to find the selected scene target.
        // 3. Use TryGetPlacementForTarget(...) to compute where the cube should go.
        // 4. Call StartAnimatedMove(destination) so the cube glides there.
        // 5. Fill in the payload fields and return JsonUtility.ToJson(payload).
        var payload = new MoveTargetPayload { targetId = targetId ?? string.Empty };

        if (!TryResolveTarget(targetId, out var room, out var candidate, out var error))
        {
            payload.status = "error";
            payload.message = error;
            payload.label = string.Empty;
            payload.name = string.Empty;
            return JsonUtility.ToJson(payload);
        }

        if (!TryGetPlacementForTarget(room, candidate.Anchor, out var destination, out error))
        {
            payload.status = "error";
            payload.message = error;
            payload.label = candidate.Info.label;
            payload.name = candidate.Info.name;
            return JsonUtility.ToJson(payload);
        }

        StartAnimatedMove(destination);

        payload.status = "ok";
        payload.message = $"Moving to {candidate.Info.name}.";
        payload.label = candidate.Info.label;
        payload.name = candidate.Info.name;
        return JsonUtility.ToJson(payload);
    }

    public string BuildVoxelObject(string targetId, string blocksJson = null)
    {
        var payload = new BuildVoxelPayload { targetId = targetId ?? string.Empty };

        // Declare all out-variables up front to avoid scope conflicts
        string error;
        MRUKRoom room;
        SceneCandidate candidate;
        Vector3 destination;
        Vector3 buildOrigin;
        Quaternion buildRotation;
        List<Vector3Int> blockOffsets;

        if (!TryResolveTarget(targetId, out room, out candidate, out error))
        {
            payload.status = "error";
            payload.message = error;
            payload.label = string.Empty;
            payload.name = string.Empty;
            payload.objectName = string.Empty;
            payload.builtBlockCount = 0;
            return JsonUtility.ToJson(payload);
        }

        if (string.IsNullOrWhiteSpace(blocksJson))
        {
            blockOffsets = new List<Vector3Int> { Vector3Int.zero };
        }
        else
        {
            if (!TryParseBlocksJson(blocksJson, out blockOffsets, out error))
            {
                payload.status = "error";
                payload.message = error;
                payload.label = candidate.Info.label;
                payload.name = candidate.Info.name;
                payload.objectName = string.Empty;
                payload.builtBlockCount = 0;
                return JsonUtility.ToJson(payload);
            }
        }

        if (!TryGetPlacementForTarget(room, candidate.Anchor, out destination, out error))
        {
            payload.status = "error";
            payload.message = error;
            payload.label = candidate.Info.label;
            payload.name = candidate.Info.name;
            payload.objectName = string.Empty;
            payload.builtBlockCount = 0;
            return JsonUtility.ToJson(payload);
        }

        if (!TryGetBuildPoseForTarget(candidate.Anchor, out buildOrigin, out buildRotation, out error))
        {
            payload.status = "error";
            payload.message = error;
            payload.label = candidate.Info.label;
            payload.name = candidate.Info.name;
            payload.objectName = string.Empty;
            payload.builtBlockCount = 0;
            return JsonUtility.ToJson(payload);
        }

        voxelBuildCounter++;
        var objectName = $"VoxelObject_{candidate.Info.label}_{voxelBuildCounter}";
        var capturedOffsets = new List<Vector3Int>(blockOffsets);

        StartAnimatedMove(destination, () =>
            SpawnVoxelObject(objectName, buildOrigin, buildRotation, capturedOffsets));

        payload.status = "ok";
        payload.message = $"Moving to {candidate.Info.name}, then building \"{objectName}\" with {capturedOffsets.Count} block(s).";
        payload.label = candidate.Info.label;
        payload.name = candidate.Info.name;
        payload.objectName = objectName;
        payload.builtBlockCount = capturedOffsets.Count;
        return JsonUtility.ToJson(payload);
    }
    private bool TryGetCurrentRoom(out MRUKRoom room, out string error)
    {
        // Helper:
        // Safely fetch the current MRUK room.
        // Returns false with an error message if MRUK is missing
        // or if the room capture data is not ready yet.
        room = null;
        error = string.Empty;

        var mruk = MRUK.Instance;
        if (mruk == null)
        {
            error = "No MRUK instance was found in the scene.";
            return false;
        }

        room = mruk.GetCurrentRoom();
        if (room == null)
        {
            error = "MRUK scene data is not ready.";
            return false;
        }

        return true;
    }

    private List<SceneCandidate> BuildSceneCandidates(MRUKRoom room)
    {
        // Helper:
        // Convert MRUK anchors into a smaller list of tool-friendly scene targets.
        // Each target stores:
        // - a stable id for tool calls
        // - the semantic label
        // - a readable name
        // - distance from the moving cube
        // - the world-space center of the anchor
        var candidates = new List<SceneCandidate>();
        if (room == null)
        {
            return candidates;
        }

        foreach (var anchor in room.Anchors)
        {
            // Keep only labels we care about for the assignment demo.
            if (anchor == null || !anchor.HasAnyLabel(candidateLabels))
            {
                continue;
            }

            // Estimate how far this target is from the moving cube
            // so we can return nearer objects first.
            var distance = anchor.GetClosestSurfacePosition(
                movingObject != null ? movingObject.transform.position : Vector3.zero,
                out _);
            if (float.IsInfinity(distance))
            {
                distance = movingObject != null
                    ? Vector3.Distance(movingObject.transform.position, anchor.GetAnchorCenter())
                    : float.PositiveInfinity;
            }

            var label = anchor.Label.ToString();
            var name = label.ToLowerInvariant().Replace("_", " ");

            candidates.Add(new SceneCandidate
            {
                Anchor = anchor,
                Info = new SceneCandidateInfo
                {
                    id = $"target_{Math.Abs(anchor.GetInstanceID())}",
                    label = label,
                    name = string.IsNullOrWhiteSpace(name) ? "target" : name,
                    distanceMeters = Mathf.Abs(distance),
                    worldCenter = anchor.GetAnchorCenter()
                }
            });
        }

        // Group by label first so repeated labels can become
        // names like "table 1" and "table 2".
        candidates.Sort((a, b) =>
        {
            var labelCompare = string.CompareOrdinal(a.Info.label, b.Info.label);
            if (labelCompare != 0)
            {
                return labelCompare;
            }

            return a.Info.distanceMeters.CompareTo(b.Info.distanceMeters);
        });

        // Count how many objects share the same label.
        var totalByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            var label = candidates[i].Info.label;
            totalByLabel.TryGetValue(label, out var total);
            totalByLabel[label] = total + 1;
        }

        // Assign per-label indices so the names are stable and easy for the LLM to use.
        var seenByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            var label = candidates[i].Info.label;
            seenByLabel.TryGetValue(label, out var seen);
            seen++;
            seenByLabel[label] = seen;

            var info = candidates[i].Info;
            info.indexWithinLabel = seen;
            if (totalByLabel[label] > 1)
            {
                info.name = $"{info.name} {seen}";
            }

            candidates[i] = new SceneCandidate
            {
                Anchor = candidates[i].Anchor,
                Info = info
            };
        }

        // Final order: nearest candidates first.
        candidates.Sort((a, b) => a.Info.distanceMeters.CompareTo(b.Info.distanceMeters));
        return candidates;
    }

    private bool TryGetPlacementForTarget(MRUKRoom room, MRUKAnchor anchor, out Vector3 destination, out string error)
    {
        // Helper:
        // Compute a simple world position for the cube next to the target.
        // We start from the closest surface point, then offset slightly outward
        // so the moving cube does not intersect the target geometry.
        destination = default;
        error = string.Empty;

        if (anchor == null)
        {
            error = "Target anchor is missing.";
            return false;
        }

        // Start from the cube's current position so the "closest surface"
        // is meaningful relative to where it is now.
        var referencePosition = movingObject != null ? movingObject.transform.position : anchor.GetAnchorCenter();
        var surfaceDistance = anchor.GetClosestSurfacePosition(referencePosition, out var closestSurface, out var normal);

        if (float.IsInfinity(surfaceDistance))
        {
            // Fallback: if MRUK cannot produce a surface point,
            // use the anchor center and a reasonable outward direction.
            closestSurface = anchor.GetAnchorCenter();
            normal = room != null ? room.GetFacingDirection(anchor) : Vector3.up;
        }

        if (normal.sqrMagnitude < 0.001f)
        {
            normal = Vector3.up;
        }

        destination = closestSurface + normal.normalized * GetPlacementOffsetAlongNormal(normal.normalized);
        return true;
    }

    private bool TryParseBlocksJson(string blocksJson, out List<Vector3Int> blockOffsets, out string error)
    {
        // Helper:
        // Parse the optional voxel layout produced by the LLM.
        // Expected format:
        // {"blocks":[{"x":0,"y":0,"z":0},{"x":1,"y":0,"z":0}]}
        // A top-level array is also accepted and wrapped automatically.
        blockOffsets = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(blocksJson))
        {
            blockOffsets = new List<Vector3Int> { Vector3Int.zero };
            return true;
        }

        var normalizedJson = blocksJson.Trim();
        if (normalizedJson.StartsWith("[", StringComparison.Ordinal))
        {
            normalizedJson = "{\"blocks\":" + normalizedJson + "}";
        }

        var input = JsonUtility.FromJson<VoxelBlocksInput>(normalizedJson);
        if (input?.blocks == null || input.blocks.Length == 0)
        {
            error = "blocksJson must contain a non-empty blocks array.";
            return false;
        }

        // Remove duplicate blocks and cap the total count
        // so the build stays small for the assignment.
        var uniqueBlocks = new HashSet<Vector3Int>();
        blockOffsets = new List<Vector3Int>(input.blocks.Length);
        for (var i = 0; i < input.blocks.Length; i++)
        {
            var block = new Vector3Int(input.blocks[i].x, input.blocks[i].y, input.blocks[i].z);
            if (uniqueBlocks.Add(block))
            {
                blockOffsets.Add(block);
                if (blockOffsets.Count >= maxVoxelBlocks)
                {
                    break;
                }
            }
        }

        if (blockOffsets.Count == 0)
        {
            error = "blocksJson did not produce any blocks.";
            return false;
        }

        return true;
    }

    private bool TryResolveTarget(string targetId, out MRUKRoom room, out SceneCandidate candidate, out string error)
    {
        // Helper:
        // Resolve a tool target id into the matching MRUK scene candidate.
        // This keeps the public tool methods shorter and easier to read.
        room = null;
        candidate = default;
        error = string.Empty;

        if (movingObject == null)
        {
            error = "Assign the A3 cube to movingObject.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            error = "Missing targetId. Use the scene candidates JSON first.";
            return false;
        }

        if (!TryGetCurrentRoom(out room, out error))
        {
            return false;
        }

        var candidates = BuildSceneCandidates(room);
        var candidateIndex = candidates.FindIndex(candidate => candidate.Info.id == targetId);
        if (candidateIndex < 0)
        {
            error = $"Unknown targetId \"{targetId}\".";
            return false;
        }

        candidate = candidates[candidateIndex];
        return true;
    }

    private bool TryGetBuildPoseForTarget(MRUKAnchor anchor, out Vector3 buildOrigin, out Quaternion buildRotation, out string error)
    {
        // Helper:
        // Choose a simple build position above the target.
        // For volume anchors, use the highest face center when possible.
        // For other anchors, fall back to the anchor transform position.
        // The voxel object stays upright to keep the build logic simple.
        buildOrigin = default;
        buildRotation = Quaternion.identity;
        error = string.Empty;

        if (anchor == null)
        {
            error = "Target anchor is missing.";
            return false;
        }

        var anchorCenter = anchor.GetAnchorCenter();
        var buildSurfacePoint = anchor.transform.position;

        if (anchor.VolumeBounds.HasValue)
        {
            var faceCenters = anchor.GetBoundsFaceCenters();
            if (faceCenters != null && faceCenters.Length > 0)
            {
                buildSurfacePoint = faceCenters[0];
                for (var i = 1; i < faceCenters.Length; i++)
                {
                    if (faceCenters[i].y > buildSurfacePoint.y)
                    {
                        buildSurfacePoint = faceCenters[i];
                    }
                }
            }
        }

        buildRotation = Quaternion.identity;
        buildOrigin = new Vector3(
            buildSurfacePoint.x,
            Mathf.Max(buildSurfacePoint.y, anchorCenter.y) + surfaceOffset + voxelCubeSize * 0.5f,
            buildSurfacePoint.z);
        return true;
    }

    private float GetPlacementOffsetAlongNormal(Vector3 normal)
    {
        // Helper:
        // Estimate how far to offset the moving cube away from the target surface.
        // This uses the cube collider size so larger cubes get pushed farther away.
        if (movingObject == null)
        {
            return surfaceOffset;
        }

        var movingTransform = movingObject.transform;
        var movingCollider = movingObject.GetComponentInChildren<Collider>();

        if (movingCollider is BoxCollider boxCollider)
        {
            var scale = movingTransform.lossyScale;
            var scaledHalfExtents = Vector3.Scale(
                boxCollider.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            return
                Mathf.Abs(Vector3.Dot(normal, movingTransform.right)) * scaledHalfExtents.x +
                Mathf.Abs(Vector3.Dot(normal, movingTransform.up)) * scaledHalfExtents.y +
                Mathf.Abs(Vector3.Dot(normal, movingTransform.forward)) * scaledHalfExtents.z +
                surfaceOffset;
        }

        if (movingCollider != null)
        {
            return Mathf.Max(
                       movingCollider.bounds.extents.x,
                       movingCollider.bounds.extents.y,
                       movingCollider.bounds.extents.z) +
                   surfaceOffset;
        }

        return surfaceOffset;
    }

    private void StartAnimatedMove(Vector3 destination, Action onArrival = null)
    {
        // Helper:
        // Start a smooth movement animation toward the destination.
        // If another move is already running, stop it first.
        // An optional callback can trigger a build action after arrival.
        if (movingObject == null)
        {
            return;
        }

        if (moveCoroutine != null)
        {
            StopCoroutine(moveCoroutine);
        }

        moveCoroutine = StartCoroutine(AnimateMoveToPosition(destination, onArrival));
    }

    private IEnumerator AnimateMoveToPosition(Vector3 destination, Action onArrival)
    {
        // Helper:
        // Move the cube a little each frame until it reaches the target.
        // Vector3.MoveTowards gives a simple constant-speed animation.
        var movingTransform = movingObject.transform;

        while (Vector3.Distance(movingTransform.position, destination) > 0.01f)
        {
            movingTransform.position = Vector3.MoveTowards(
                movingTransform.position,
                destination,
                moveSpeed * Time.deltaTime);
            yield return null;
        }

        movingTransform.position = destination;
        moveCoroutine = null;
        onArrival?.Invoke();
    }

    private void SpawnVoxelObject(string objectName, Vector3 buildOrigin, Quaternion buildRotation, IReadOnlyList<Vector3Int> blockOffsets)
    {
        // Helper:
        // Create a parent object and then instantiate one Unity cube per voxel offset.
        // Each block offset is interpreted on a simple voxel grid whose unit size
        // is voxelCubeSize.
        var parent = new GameObject(objectName);
        parent.transform.SetParent(voxelObjectsRoot != null ? voxelObjectsRoot : transform, worldPositionStays: false);
        parent.transform.SetPositionAndRotation(buildOrigin, buildRotation);

        for (var i = 0; i < blockOffsets.Count; i++)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"Voxel_{i}";
            cube.transform.SetParent(parent.transform, worldPositionStays: false);
            cube.transform.localScale = Vector3.one * voxelCubeSize;
            cube.transform.localPosition = Vector3.Scale((Vector3)blockOffsets[i], Vector3.one * voxelCubeSize);

            if (voxelMaterial != null)
            {
                var renderer = cube.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = voxelMaterial;
                }
            }
        }
    }
}
