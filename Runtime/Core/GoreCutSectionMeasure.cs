using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoreLab
{
    /// <summary>
    /// Where the cut of a prepared (naked) mesh crosses the limb and how wide the limb is there, from the points of its cut line: the middle of
    /// the points and the typical distance from it, both medians so a few stray points do not move them. A worn piece has to cross the cut near
    /// there, and as a ring of about that size, to be cut by it (see <see cref="GoreMeshPipeline"/>).
    /// </summary>
    public static class GoreCutSectionMeasure
    {
        private const int MinPoints = 6;

        /// <summary>The centre and the radius of the cut line. False when there are too few points.</summary>
        public static bool TryMeasure(IReadOnlyList<Vector3> points, out Vector3 center, out float radius)
        {
            center = Vector3.zero;
            radius = 0f;
            if (points == null || points.Count < MinPoints)
            {
                return false;
            }

            var count = points.Count;
            var values = new float[count];
            for (var axis = 0; axis < 3; axis++)
            {
                for (var i = 0; i < count; i++)
                {
                    values[i] = points[i][axis];
                }

                Array.Sort(values);
                center[axis] = values[count / 2];
            }

            for (var i = 0; i < count; i++)
            {
                values[i] = (points[i] - center).magnitude;
            }

            Array.Sort(values);
            radius = values[count / 2];
            return radius > 1e-6f;
        }
    }
}
