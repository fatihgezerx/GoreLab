using System;

namespace GoreLab
{
    /// <summary>
    /// The rules for the pieces a user draws by hand on a static mesh, as plain arrays (no Unity objects, so they can be checked on their own).
    /// A piece is a number; every triangle of the mesh (flat number) has an owner: a piece, or -1 for the rest.
    /// </summary>
    internal static class GoreStaticPieces
    {
        /// <summary>
        /// Marks the triangles whose three corners are all in the selection (<paramref name="selectedGroup"/> is per welded vertex group,
        /// <paramref name="group"/> gives the group of every vertex). Returns how many triangles were marked.
        /// </summary>
        internal static int FacesOf(int[] corners, int[] group, bool[] selectedGroup, bool[] faces)
        {
            var count = 0;
            var triangles = corners.Length / 3;
            for (var t = 0; t < triangles; t++)
            {
                var on = selectedGroup[group[corners[t * 3]]] && selectedGroup[group[corners[t * 3 + 1]]] && selectedGroup[group[corners[t * 3 + 2]]];
                faces[t] = on;
                if (on)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Counts the triangles of every piece. <paramref name="counts"/> needs <paramref name="pieceCount"/> + 1 entries; the last one is the rest.</summary>
        internal static void Count(int[] owner, int pieceCount, int[] counts)
        {
            Array.Clear(counts, 0, counts.Length);
            for (var t = 0; t < owner.Length; t++)
            {
                var piece = owner[t];
                counts[piece >= 0 && piece < pieceCount ? piece : pieceCount]++;
            }
        }

        /// <summary>Makes a new piece of the marked triangles (they leave the piece they were in). Returns how many were moved; with none, no piece is made.</summary>
        internal static int NewPiece(int[] owner, bool[] faces, ref int pieceCount)
        {
            var moved = 0;
            for (var t = 0; t < owner.Length; t++)
            {
                if (faces[t])
                {
                    owner[t] = pieceCount;
                    moved++;
                }
            }

            if (moved > 0)
            {
                pieceCount++;
            }

            return moved;
        }

        /// <summary>Gives the marked triangles to <paramref name="piece"/> (they leave the piece they were in). Returns how many changed owner.</summary>
        internal static int Assign(int[] owner, bool[] faces, int piece)
        {
            var moved = 0;
            for (var t = 0; t < owner.Length; t++)
            {
                if (faces[t] && owner[t] != piece)
                {
                    owner[t] = piece;
                    moved++;
                }
            }

            return moved;
        }

        /// <summary>Takes the marked triangles that belong to <paramref name="piece"/> out of it (they become the rest). Returns how many were taken out.</summary>
        internal static int Remove(int[] owner, bool[] faces, int piece)
        {
            var moved = 0;
            for (var t = 0; t < owner.Length; t++)
            {
                if (faces[t] && owner[t] == piece)
                {
                    owner[t] = -1;
                    moved++;
                }
            }

            return moved;
        }

        /// <summary>Deletes a piece: its triangles become the rest, and the pieces after it move down one number.</summary>
        internal static void Delete(int[] owner, ref int pieceCount, int piece)
        {
            if (piece < 0 || piece >= pieceCount)
            {
                return;
            }

            for (var t = 0; t < owner.Length; t++)
            {
                if (owner[t] == piece)
                {
                    owner[t] = -1;
                }
                else if (owner[t] > piece)
                {
                    owner[t]--;
                }
            }

            pieceCount--;
        }

        /// <summary>How many pieces have at least one triangle (the last entry of <paramref name="counts"/> is the rest and is not counted).</summary>
        internal static int NonEmpty(int[] counts, int pieceCount)
        {
            var filled = 0;
            for (var p = 0; p < pieceCount; p++)
            {
                if (counts[p] > 0)
                {
                    filled++;
                }
            }

            return filled;
        }

        /// <summary>The number of pieces the owners go up to (the highest owner plus one; 0 when every triangle is the rest).</summary>
        internal static int PieceCountOf(int[] owner)
        {
            var highest = -1;
            for (var t = 0; t < owner.Length; t++)
            {
                if (owner[t] > highest)
                {
                    highest = owner[t];
                }
            }

            return highest + 1;
        }
    }
}
