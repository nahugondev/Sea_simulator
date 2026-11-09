using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SeaSim.Fish
{
    /// <summary>
    /// A dense uniform grid over a bounded box. Fish are sorted by cell every frame (counting sort), so all the
    /// fish of a cell, and of a whole row of cells, sit next to each other in memory. Neighbour searches then read
    /// contiguous data instead of following linked lists through a hash map, which is much friendlier to the CPU cache.
    /// Cells are numbered x first, so the 3 cells of a row (x-1, x, x+1) form a single contiguous range.
    /// Positions outside the box are clamped to the border cells.
    /// </summary>
    public struct GridLayout
    {
        public float3 origin;
        public float cellSize;
        public float invCellSize;
        public int3 dims;

        public int CellCount => dims.x * dims.y * dims.z;

        /// <summary>Covers <paramref name="boxSize"/> around <paramref name="center"/>, plus a border of 2 cells.</summary>
        public static GridLayout Create(float3 center, float3 boxSize, float cellSize, int maxCells)
        {
            float3 size = boxSize + 4f * cellSize;
            int3 dims = (int3)math.ceil(size / cellSize);
            // Safety net for tiny cells in a huge volume: grow the cells until the grid fits in memory.
            while ((long)dims.x * dims.y * dims.z > maxCells)
            {
                cellSize *= 1.25f;
                size = boxSize + 4f * cellSize;
                dims = (int3)math.ceil(size / cellSize);
            }
            return new GridLayout
            {
                origin = center - size * 0.5f,
                cellSize = cellSize,
                invCellSize = 1f / cellSize,
                dims = math.max(dims, 1),
            };
        }

        public int3 CellCoord(float3 p) => math.clamp((int3)math.floor((p - origin) * invCellSize), 0, dims - 1);
        public int Flatten(int3 c) => c.x + dims.x * (c.y + dims.y * c.z);
    }

    /// <summary>Step 1 (parallel): which cell each fish is in.</summary>
    [BurstCompile]
    struct CellIndexJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [WriteOnly] public NativeArray<int> fishCell;
        public GridLayout layout;

        public void Execute(int i) => fishCell[i] = layout.Flatten(layout.CellCoord(positions[i]));
    }

    /// <summary>
    /// Step 2 (single thread, O(fish + cells)): counting sort.
    /// cellStart[c] .. cellStart[c + 1] is the range of cell c in the sorted order; order[t] is the original index
    /// of the fish at sorted position t.
    /// </summary>
    [BurstCompile]
    struct CountingSortJob : IJob
    {
        [ReadOnly] public NativeArray<int> fishCell;
        public NativeArray<int> cellStart;   // CellCount + 1
        public NativeArray<int> cellCursor;  // CellCount
        [WriteOnly] public NativeArray<int> order;

        public void Execute()
        {
            int cells = cellCursor.Length;
            for (int c = 0; c <= cells; c++) cellStart[c] = 0;
            for (int i = 0; i < fishCell.Length; i++) cellStart[fishCell[i] + 1]++;
            for (int c = 1; c <= cells; c++) cellStart[c] += cellStart[c - 1];
            for (int c = 0; c < cells; c++) cellCursor[c] = cellStart[c];
            for (int i = 0; i < fishCell.Length; i++) order[cellCursor[fishCell[i]]++] = i;
        }
    }

    /// <summary>Step 3 (parallel): copies the fish data into sorted order, so neighbour reads are contiguous.</summary>
    [BurstCompile]
    struct GatherSortedJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> order;
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float> scales;
        [ReadOnly] public NativeArray<float> panic;
        [WriteOnly] public NativeArray<float3> sortedPositions;
        [WriteOnly] public NativeArray<float3> sortedVelocities;
        [WriteOnly] public NativeArray<float> sortedScales;
        [WriteOnly] public NativeArray<float> sortedPanic;

        public void Execute(int t)
        {
            int i = order[t];
            sortedPositions[t] = positions[i];
            sortedVelocities[t] = velocities[i];
            sortedScales[t] = scales[i];
            sortedPanic[t] = panic[i];
        }
    }
}
