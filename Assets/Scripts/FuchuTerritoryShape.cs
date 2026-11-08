using System;
using System.Collections.Generic;
using Clipper2Lib;
using UnityEngine;

/// <summary>Stores the union of all claimed ground and closes new trails against its contours.</summary>
internal sealed class FuchuTerritoryShape
{
    private const double UnitsPerMeter = 1000.0;
    private const double UnitsPerSquareMeter = UnitsPerMeter * UnitsPerMeter;
    private const double MinimumArea = 2.0 * UnitsPerSquareMeter;
    private Paths64 paths = new Paths64();
    private readonly List<List<Vector2>> contours = new List<List<Vector2>>();
    private readonly List<Rect> bounds = new List<Rect>();
    private Contact? departure;
    private bool walkedOutside;

    public int Version { get; private set; }
    public double ClaimedSquareMeters { get; private set; }
    public IReadOnlyList<List<Vector2>> Contours => contours;

    public bool Overlaps(Rect rectangle)
    {
        foreach (Rect contourBounds in bounds)
            if (contourBounds.Overlaps(rectangle)) return true;
        return false;
    }

    public bool Contains(Vector2 point)
    {
        bool inside = false;
        Point64 sample = ToPoint(point);
        for (int i = 0; i < paths.Count; i++)
        {
            Rect rectangle = bounds[i];
            if (point.x < rectangle.xMin || point.x > rectangle.xMax ||
                point.y < rectangle.yMin || point.y > rectangle.yMax) continue;
            PointInPolygonResult result = Clipper.PointInPolygon(sample, paths[i]);
            if (result == PointInPolygonResult.IsOn) return true;
            if (result == PointInPolygonResult.IsInside) inside = !inside;
        }
        return inside;
    }

    public void ClearOpenTrail()
    {
        departure = null;
        walkedOutside = false;
    }

    public bool TryClaimLoop(List<Vector2> polygon, out Paths64 triangles)
    {
        return TryClaim(Normalize(ToPath(polygon)), out triangles);
    }

    public bool TryClaimAgainstBoundary(List<Vector2> trail, Vector2 point, float snapDistance, out Paths64 triangles)
    {
        triangles = null;
        if (contours.Count == 0) return false;
        Vector2 last = trail.Count > 0 ? trail[trail.Count - 1] : point;
        List<Contact> contacts = FindContacts(last, point, trail.Count, snapDistance);
        float previous = 0f;
        foreach (Contact contact in contacts)
        {
            RecordOutsideSegment(last, point, previous, contact.AlongStep);
            if (walkedOutside && departure is Contact start && start.Contour == contact.Contour &&
                trail.Count - start.TrailIndex >= 2)
            {
                Paths64 forward = Normalize(FollowBoundary(trail, start, contact, 1));
                Paths64 backward = Normalize(FollowBoundary(trail, start, contact, -1));
                double forwardArea = Clipper.Area(Clipper.Difference(forward, paths, FillRule.NonZero));
                double backwardArea = Clipper.Area(Clipper.Difference(backward, paths, FillRule.NonZero));
                // The trail can close along either side of a contour. Claim the smaller new pocket.
                Paths64 candidate = backwardArea >= MinimumArea &&
                    (forwardArea < MinimumArea || backwardArea < forwardArea) ? backward : forward;
                if (TryClaim(candidate, out triangles)) return true;
            }
            departure = contact;
            walkedOutside = false;
            previous = contact.AlongStep;
        }
        RecordOutsideSegment(last, point, previous, 1f);
        return false;
    }

    private void RecordOutsideSegment(Vector2 from, Vector2 to, float start, float end)
    {
        if (departure.HasValue && end > start && !Contains(Vector2.Lerp(from, to, (start + end) * 0.5f)))
            walkedOutside = true;
    }

    private List<Contact> FindContacts(Vector2 last, Vector2 point, int trailIndex, float snapDistance)
    {
        var contacts = new List<Contact>();
        Contact? nearest = null;
        float nearestDistance = snapDistance * snapDistance;
        Vector2 step = point - last;
        Vector2 margin = Vector2.one * snapDistance;
        Vector2 min = Vector2.Min(last, point) - margin;
        Vector2 max = Vector2.Max(last, point) + margin;
        Rect search = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        for (int i = 0; i < contours.Count; i++)
        {
            if (!bounds[i].Overlaps(search)) continue;
            List<Vector2> contour = contours[i];
            for (int edgeIndex = 0; edgeIndex < contour.Count; edgeIndex++)
            {
                Vector2 a = contour[edgeIndex];
                Vector2 edge = contour[(edgeIndex + 1) % contour.Count] - a;
                if (TryIntersect(last, step, a, edge, out float along))
                    contacts.Add(new Contact(i, edgeIndex, trailIndex, last + step * along, along));

                if (edge.sqrMagnitude == 0f) continue;
                Vector2 closest = a + edge * Mathf.Clamp01(Vector2.Dot(point - a, edge) / edge.sqrMagnitude);
                float distance = (point - closest).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = new Contact(i, edgeIndex, trailIndex, closest, 1f);
            }
        }
        if (contacts.Count == 0 && nearest.HasValue)
            contacts.Add(nearest.Value);
        contacts.Sort((a, b) => a.AlongStep.CompareTo(b.AlongStep));
        return contacts;
    }

    internal static bool TryIntersect(Vector2 last, Vector2 step, Vector2 a, Vector2 edge, out float along)
    {
        float determinant = Cross(step, edge);
        along = 0f;
        if (Mathf.Abs(determinant) < 0.000001f) return false;
        Vector2 separation = a - last;
        along = Cross(separation, edge) / determinant;
        float alongEdge = Cross(separation, step) / determinant;
        return along > 0.0001f && along <= 1f && alongEdge >= 0f && alongEdge <= 1f;
    }

    private Path64 FollowBoundary(List<Vector2> trail, Contact start, Contact end, int direction)
    {
        List<Vector2> contour = contours[start.Contour];
        var polygon = new Path64 { ToPoint(InsideEdge(contour, start)) };
        for (int i = start.TrailIndex; i < trail.Count; i++)
            polygon.Add(ToPoint(trail[i]));
        polygon.Add(ToPoint(InsideEdge(contour, end)));

        int edge = end.Edge;
        Vector2 edgeDirection = contour[(edge + 1) % contour.Count] - contour[edge];
        if (edge == start.Edge && direction * Vector2.Dot(start.Point - end.Point, edgeDirection) >= 0f)
            return polygon;
        do
        {
            polygon.Add(ToPoint(contour[direction > 0 ? (edge + 1) % contour.Count : edge]));
            edge = (edge + direction + contour.Count) % contour.Count;
        } while (edge != start.Edge);
        return polygon;
    }

    private static Vector2 InsideEdge(List<Vector2> contour, Contact contact)
    {
        // Extend the join a few millimeters into the filled side so rounded coordinates overlap.
        Vector2 edge = contour[(contact.Edge + 1) % contour.Count] - contour[contact.Edge];
        return contact.Point + new Vector2(-edge.y, edge.x).normalized * 0.004f;
    }

    private static Paths64 Normalize(Path64 path) => Clipper.Union(new Paths64 { path }, FillRule.EvenOdd);

    private bool TryClaim(Paths64 candidate, out Paths64 triangles)
    {
        triangles = null;
        if (candidate.Count == 0) return false;
        Paths64 union = Clipper.Union(paths, candidate, FillRule.NonZero);
        double area = Clipper.Area(union);
        if (area - ClaimedSquareMeters * UnitsPerSquareMeter < MinimumArea) return false;
        if (Clipper.Triangulate(union, out triangles, false) != TriangulateResult.success)
            return false;
        paths = union;
        ClaimedSquareMeters = area / UnitsPerSquareMeter;
        contours.Clear();
        bounds.Clear();
        foreach (Path64 path in paths)
        {
            var contour = new List<Vector2>(path.Count);
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
            foreach (Point64 vertex in path)
            {
                Vector2 point = ToVector(vertex);
                contour.Add(point);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            contours.Add(contour);
            bounds.Add(Rect.MinMaxRect(min.x, min.y, max.x, max.y));
        }
        Version++;
        ClearOpenTrail();
        return true;
    }

    private static Path64 ToPath(List<Vector2> points)
    {
        var path = new Path64(points.Count);
        foreach (Vector2 point in points) path.Add(ToPoint(point));
        return path;
    }

    private static Point64 ToPoint(Vector2 point) =>
        new Point64((long)Math.Round(point.x * UnitsPerMeter), (long)Math.Round(point.y * UnitsPerMeter));

    public static Vector2 ToVector(Point64 point) =>
        new Vector2((float)(point.X / UnitsPerMeter), (float)(point.Y / UnitsPerMeter));

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    private readonly struct Contact
    {
        public readonly int Contour, Edge, TrailIndex;
        public readonly Vector2 Point;
        public readonly float AlongStep;

        public Contact(int contour, int edge, int trailIndex, Vector2 point, float alongStep)
        {
            Contour = contour;
            Edge = edge;
            TrailIndex = trailIndex;
            Point = point;
            AlongStep = alongStep;
        }
    }
}
