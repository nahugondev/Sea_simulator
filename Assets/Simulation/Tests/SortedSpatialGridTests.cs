using NUnit.Framework;
using SeaSim.Fish;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SeaSim.Tests
{
    public class SortedSpatialGridTests
    {
        [Test]
        public void Create_CoversTheBoxPlusBorder()
        {
            var layout = GridLayout.Create(float3.zero, new float3(10f, 4f, 6f), 1f, 1_000_000);

            // 2 cells of border on each side.
            Assert.AreEqual(new int3(14, 8, 10), layout.dims);
            Assert.AreEqual(new int3(2, 2, 2), layout.CellCoord(new float3(-5f, -2f, -3f) + 0.01f));
        }

        [Test]
        public void Create_GrowsCellsToStayUnderTheCellBudget()
        {
            const int maxCells = 1000;
            var layout = GridLayout.Create(float3.zero, new float3(100f), 0.5f, maxCells);

            Assert.LessOrEqual(layout.CellCount, maxCells);
            Assert.Greater(layout.cellSize, 0.5f);
        }

        [Test]
        public void CellCoord_ClampsPositionsOutsideTheBox()
        {
            var layout = GridLayout.Create(float3.zero, new float3(10f), 1f, 1_000_000);

            Assert.AreEqual(int3.zero, layout.CellCoord(new float3(-1000f)));
            Assert.AreEqual(layout.dims - 1, layout.CellCoord(new float3(1000f)));
        }

        [Test]
        public void Flatten_GivesEachCellAUniqueIndex()
        {
            var layout = GridLayout.Create(float3.zero, new float3(3f, 2f, 4f), 1f, 1_000_000);
            var seen = new bool[layout.CellCount];
            for (int z = 0; z < layout.dims.z; z++)
            for (int y = 0; y < layout.dims.y; y++)
            for (int x = 0; x < layout.dims.x; x++)
            {
                int i = layout.Flatten(new int3(x, y, z));
                Assert.IsFalse(seen[i], $"Cell index {i} used twice");
                seen[i] = true;
            }
        }

        [Test]
        public void CountingSort_GroupsFishByCell()
        {
            const int fish = 5000;
            var layout = GridLayout.Create(float3.zero, new float3(20f), 1f, 1_000_000);
            var random = new Random(1234);

            var positions = new NativeArray<float3>(fish, Allocator.TempJob);
            var fishCell = new NativeArray<int>(fish, Allocator.TempJob);
            var cellStart = new NativeArray<int>(layout.CellCount + 1, Allocator.TempJob);
            var cellCursor = new NativeArray<int>(layout.CellCount, Allocator.TempJob);
            var order = new NativeArray<int>(fish, Allocator.TempJob);
            try
            {
                for (int i = 0; i < fish; i++) positions[i] = random.NextFloat3(-12f, 12f); // some outside the box
                var cells = new CellIndexJob { positions = positions, fishCell = fishCell, layout = layout }
                    .Schedule(fish, 64);
                new CountingSortJob { fishCell = fishCell, cellStart = cellStart, cellCursor = cellCursor, order = order }
                    .Schedule(cells).Complete();

                // order is a permutation of the fish.
                var used = new bool[fish];
                foreach (int i in order)
                {
                    Assert.IsFalse(used[i], $"Fish {i} sorted twice");
                    used[i] = true;
                }

                // Each cell's range holds exactly the fish of that cell.
                Assert.AreEqual(0, cellStart[0]);
                Assert.AreEqual(fish, cellStart[layout.CellCount]);
                for (int c = 0; c < layout.CellCount; c++)
                for (int t = cellStart[c]; t < cellStart[c + 1]; t++)
                    Assert.AreEqual(c, fishCell[order[t]]);
            }
            finally
            {
                positions.Dispose();
                fishCell.Dispose();
                cellStart.Dispose();
                cellCursor.Dispose();
                order.Dispose();
            }
        }
    }
}
