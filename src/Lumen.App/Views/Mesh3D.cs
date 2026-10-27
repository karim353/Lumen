using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Media3D;

namespace Lumen.Views;

/// <summary>
/// Генерация геометрии для 3D-сцены: скруглённые плиты с фаской (корпус монитора, подставка),
/// вытянутые профили (Light Bar), цилиндры и кольца (пульт), плотные сетки-плоскости
/// (стена и стол — WPF считает освещение по вершинам, и пятно прожектора видно только на частой сетке).
/// Нормали у скруглений общие — блики скользят по краям плавно, а не гранями.
/// </summary>
internal static class Mesh3D
{
    /// <summary>Точка контура и внешняя нормаль в ней.</summary>
    public readonly record struct ProfilePoint(Point P, Vector N);

    /// <summary>Скруглённый прямоугольник против часовой стрелки, с центром в нуле.</summary>
    public static List<ProfilePoint> RoundedRect(double width, double height, double radius, int segments = 6)
    {
        var r = Math.Clamp(radius, 0.001, Math.Min(width, height) / 2);
        double cx = width / 2 - r, cy = height / 2 - r;
        var corners = new[] { (cx, cy, 0.0), (-cx, cy, 90.0), (-cx, -cy, 180.0), (cx, -cy, 270.0) };

        var result = new List<ProfilePoint>();
        foreach (var (x, y, start) in corners)
        {
            for (var i = 0; i <= segments; i++)
            {
                var a = (start + 90.0 * i / segments) * Math.PI / 180;
                var n = new Vector(Math.Cos(a), Math.Sin(a));
                result.Add(new ProfilePoint(new Point(x + n.X * r, y + n.Y * r), n));
            }
        }
        return result;
    }

    /// <summary>
    /// Плита: контур в плоскости XY, толщина по Z (от −d/2 до +d/2), фаска по лицевой и задней кромке.
    /// </summary>
    public static MeshGeometry3D Slab(double width, double height, double depth, double radius, double bevel, int segments = 6)
    {
        var mesh = new MeshGeometry3D();
        var profile = RoundedRect(width, height, radius, segments);
        var count = profile.Count;
        bevel = Math.Clamp(bevel, 0, Math.Min(depth / 2.5, radius * 0.9));
        double zf = depth / 2, zb = -depth / 2;
        var forward = new Vector3D(0, 0, 1);

        // Кольца вершин: задняя фаска (внутр.), бок сзади, бок спереди, передняя фаска (внутр.)
        var backInner = AddRing(mesh, profile, -bevel, zb, n => Normalize(N3(n) * 0.55 - forward * 0.85));
        var backSide = AddRing(mesh, profile, 0, zb + bevel, N3);
        var frontSide = AddRing(mesh, profile, 0, zf - bevel, N3);
        var frontInner = AddRing(mesh, profile, -bevel, zf, n => Normalize(N3(n) * 0.55 + forward * 0.85));

        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var outward = N3(profile[i].N);
            Quad(mesh, backSide + i, backSide + j, frontSide + j, frontSide + i, outward);
            Quad(mesh, frontSide + i, frontSide + j, frontInner + j, frontInner + i, outward + forward);
            Quad(mesh, backInner + i, backInner + j, backSide + j, backSide + i, outward - forward);
        }

        // Крышки: свои вершины с нормалью строго ±Z (чёткая лицевая плоскость)
        Cap(mesh, profile, -bevel, zf, forward);
        Cap(mesh, profile, -bevel, zb, -forward);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Контур в плоскости YZ (P.X → Y, P.Y → Z), вытянутый по X; торцы закрыты.</summary>
    public static MeshGeometry3D ExtrudeX(List<ProfilePoint> profile, double x0, double x1)
    {
        var mesh = new MeshGeometry3D();
        var count = profile.Count;
        var start = mesh.Positions.Count;
        foreach (var x in new[] { x0, x1 })
        {
            foreach (var p in profile)
            {
                mesh.Positions.Add(new Point3D(x, p.P.X, p.P.Y));
                mesh.Normals.Add(new Vector3D(0, p.N.X, p.N.Y));
                mesh.TextureCoordinates.Add(new Point((x - x0) / (x1 - x0), 0.5));
            }
        }
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            Quad(mesh, start + i, start + j, start + count + j, start + count + i,
                new Vector3D(0, profile[i].N.X, profile[i].N.Y));
        }

        foreach (var (x, n) in new[] { (x0, new Vector3D(-1, 0, 0)), (x1, new Vector3D(1, 0, 0)) })
        {
            var center = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(x, 0, 0));
            mesh.Normals.Add(n);
            mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
            foreach (var p in profile)
            {
                mesh.Positions.Add(new Point3D(x, p.P.X, p.P.Y));
                mesh.Normals.Add(n);
                mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
            }
            for (var i = 0; i < count; i++)
                Tri(mesh, center, center + 1 + i, center + 1 + (i + 1) % count, n);
        }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Прямоугольная сетка nu×nv клеток: origin + u·s + v·t, UV — от 0 до 1.</summary>
    public static MeshGeometry3D Grid(Point3D origin, Vector3D u, Vector3D v, int nu, int nv)
    {
        var mesh = new MeshGeometry3D();
        var normal = Normalize(Vector3D.CrossProduct(u, v));
        for (var j = 0; j <= nv; j++)
        for (var i = 0; i <= nu; i++)
        {
            double s = (double)i / nu, t = (double)j / nv;
            mesh.Positions.Add(origin + u * s + v * t);
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(new Point(s, 1 - t));
        }
        for (var j = 0; j < nv; j++)
        for (var i = 0; i < nu; i++)
        {
            var a = j * (nu + 1) + i;
            Quad(mesh, a, a + 1, a + nu + 2, a + nu + 1, normal);
        }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Цилиндр по оси Y: основание на y0, высота h, с верхней крышкой.</summary>
    public static MeshGeometry3D Cylinder(Point3D bottomCenter, double radius, double height, int segments = 24)
    {
        var mesh = new MeshGeometry3D();
        for (var k = 0; k < 2; k++)
        for (var i = 0; i <= segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            var n = new Vector3D(Math.Cos(a), 0, Math.Sin(a));
            mesh.Positions.Add(bottomCenter + n * radius + new Vector3D(0, k * height, 0));
            mesh.Normals.Add(n);
            mesh.TextureCoordinates.Add(new Point((double)i / segments, k));
        }
        for (var i = 0; i < segments; i++)
            Quad(mesh, i, i + 1, segments + 2 + i, segments + 1 + i, mesh.Normals[i]);

        var up = new Vector3D(0, 1, 0);
        var center = mesh.Positions.Count;
        mesh.Positions.Add(bottomCenter + up * height);
        mesh.Normals.Add(up);
        mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        for (var i = 0; i <= segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            mesh.Positions.Add(bottomCenter + new Vector3D(Math.Cos(a) * radius, height, Math.Sin(a) * radius));
            mesh.Normals.Add(up);
            mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        }
        for (var i = 0; i < segments; i++) Tri(mesh, center, center + 1 + i, center + 2 + i, up);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Плоское кольцо, смотрящее вверх (светящийся ободок пульта).</summary>
    public static MeshGeometry3D Ring(Point3D center, double inner, double outer, int segments = 48)
    {
        var mesh = new MeshGeometry3D();
        var up = new Vector3D(0, 1, 0);
        for (var i = 0; i <= segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            var d = new Vector3D(Math.Cos(a), 0, Math.Sin(a));
            mesh.Positions.Add(center + d * inner);
            mesh.Positions.Add(center + d * outer);
            mesh.Normals.Add(up);
            mesh.Normals.Add(up);
            mesh.TextureCoordinates.Add(new Point((double)i / segments, 0));
            mesh.TextureCoordinates.Add(new Point((double)i / segments, 1));
        }
        for (var i = 0; i < segments; i++)
            Quad(mesh, 2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1, up);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Эллипсоид (мышь, листья растения): UV-сфера со сжатыми осями и честными нормалями.</summary>
    public static MeshGeometry3D Ellipsoid(Point3D center, double rx, double ry, double rz, int slices = 28, int stacks = 16)
    {
        var mesh = new MeshGeometry3D();
        for (var j = 0; j <= stacks; j++)
        {
            var v = Math.PI * j / stacks;                // 0 — верх, π — низ
            for (var i = 0; i <= slices; i++)
            {
                var u = 2 * Math.PI * i / slices;
                var dir = new Vector3D(Math.Sin(v) * Math.Cos(u), Math.Cos(v), Math.Sin(v) * Math.Sin(u));
                mesh.Positions.Add(center + new Vector3D(dir.X * rx, dir.Y * ry, dir.Z * rz));
                // Нормаль эллипсоида: градиент x²/a² + y²/b² + z²/c²
                mesh.Normals.Add(Normalize(new Vector3D(dir.X / rx, dir.Y / ry, dir.Z / rz)));
                mesh.TextureCoordinates.Add(new Point((double)i / slices, (double)j / stacks));
            }
        }
        for (var j = 0; j < stacks; j++)
        for (var i = 0; i < slices; i++)
        {
            var a = j * (slices + 1) + i;
            var b = a + slices + 1;
            var outward = mesh.Positions[a] - center;
            Quad(mesh, a, a + 1, b + 1, b, outward);
        }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Тор в плоскости XY (ручка кружки): R — радиус кольца, r — толщина.</summary>
    public static MeshGeometry3D Torus(Point3D center, double r1, double r2, int major = 28, int minor = 12)
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i <= major; i++)
        {
            var u = 2 * Math.PI * i / major;
            var ring = new Vector3D(Math.Cos(u), Math.Sin(u), 0);
            for (var j = 0; j <= minor; j++)
            {
                var w = 2 * Math.PI * j / minor;
                var n = ring * Math.Cos(w) + new Vector3D(0, 0, Math.Sin(w));
                mesh.Positions.Add(center + ring * r1 + n * r2);
                mesh.Normals.Add(n);
                mesh.TextureCoordinates.Add(new Point((double)i / major, (double)j / minor));
            }
        }
        for (var i = 0; i < major; i++)
        for (var j = 0; j < minor; j++)
        {
            var a = i * (minor + 1) + j;
            var b = a + minor + 1;
            Quad(mesh, a, b, b + 1, a + 1, mesh.Normals[a]);
        }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Четырёхугольник a-b-c-d с UV (0,1)-(1,1)-(1,0)-(0,0), лицом к normal.</summary>
    public static MeshGeometry3D Quad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
    {
        var mesh = new MeshGeometry3D();
        foreach (var (p, uv) in new[] { (a, new Point(0, 1)), (b, new Point(1, 1)), (c, new Point(1, 0)), (d, new Point(0, 0)) })
        {
            mesh.Positions.Add(p);
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(uv);
        }
        Quad(mesh, 0, 1, 2, 3, normal);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>
    /// Склеивает неподвижные непрозрачные модели с общим (замороженным) материалом в одну сетку,
    /// запекая их трансформации в вершины. WPF тратит процессор на каждую отдельную модель,
    /// поэтому 40 объектов одной сцены превращаются примерно в 15.
    /// Склеенная модель встаёт на место первой модели своего материала — порядок отрисовки
    /// (стена и стол раньше прозрачных пятен и теней на них) сохраняется. Остальные модели
    /// (светящиеся, прозрачные, с меняющимися кистями) не трогаются.
    /// </summary>
    public static Model3DGroup MergeByMaterial(Model3DGroup source)
    {
        var result = new Model3DGroup();
        var merged = new Dictionary<Material, MeshGeometry3D>();

        foreach (var child in source.Children)
        {
            if (child is not GeometryModel3D { Material: { IsFrozen: true } material, BackMaterial: null, Geometry: MeshGeometry3D mesh } model)
            {
                result.Children.Add(child);
                continue;
            }

            if (!merged.TryGetValue(material, out var target))
            {
                target = new MeshGeometry3D();
                merged[material] = target;
                result.Children.Add(new GeometryModel3D(target, material));
            }
            Append(target, mesh, model.Transform?.Value ?? Matrix3D.Identity);
        }

        foreach (var mesh in merged.Values) mesh.Freeze();
        return result;
    }

    private static void Append(MeshGeometry3D target, MeshGeometry3D source, Matrix3D matrix)
    {
        var offset = target.Positions.Count;
        foreach (var p in source.Positions) target.Positions.Add(matrix.Transform(p));
        foreach (var n in source.Normals)
        {
            var v = matrix.Transform(n); // вектор: перенос не влияет; в сцене только повороты и сдвиги
            if (v.LengthSquared > 0) v.Normalize();
            target.Normals.Add(v);
        }
        foreach (var uv in source.TextureCoordinates) target.TextureCoordinates.Add(uv);
        foreach (var i in source.TriangleIndices) target.TriangleIndices.Add(offset + i);
    }

    // ---------- Вспомогательное ----------

    private static int AddRing(MeshGeometry3D mesh, List<ProfilePoint> profile, double inset, double z, Func<Vector, Vector3D> normal)
    {
        var start = mesh.Positions.Count;
        foreach (var p in profile)
        {
            var q = p.P + p.N * inset;
            mesh.Positions.Add(new Point3D(q.X, q.Y, z));
            mesh.Normals.Add(normal(p.N));
            mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        }
        return start;
    }

    private static void Cap(MeshGeometry3D mesh, List<ProfilePoint> profile, double inset, double z, Vector3D normal)
    {
        // UV по габаритам контура — на крышку можно положить текстуру
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in profile)
        {
            minX = Math.Min(minX, p.P.X); maxX = Math.Max(maxX, p.P.X);
            minY = Math.Min(minY, p.P.Y); maxY = Math.Max(maxY, p.P.Y);
        }

        var center = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, 0, z));
        mesh.Normals.Add(normal);
        mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        foreach (var p in profile)
        {
            var q = p.P + p.N * inset;
            mesh.Positions.Add(new Point3D(q.X, q.Y, z));
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(new Point((q.X - minX) / (maxX - minX), 1 - (q.Y - minY) / (maxY - minY)));
        }
        for (var i = 0; i < profile.Count; i++)
            Tri(mesh, center, center + 1 + i, center + 1 + (i + 1) % profile.Count, normal);
    }

    private static void Quad(MeshGeometry3D mesh, int a, int b, int c, int d, Vector3D facing)
    {
        Tri(mesh, a, b, c, facing);
        Tri(mesh, a, c, d, facing);
    }

    /// <summary>Треугольник, развёрнутый лицом к facing (WPF рисует лицевую сторону против часовой).</summary>
    private static void Tri(MeshGeometry3D mesh, int a, int b, int c, Vector3D facing)
    {
        var pa = mesh.Positions[a];
        var n = Vector3D.CrossProduct(mesh.Positions[b] - pa, mesh.Positions[c] - pa);
        if (Vector3D.DotProduct(n, facing) < 0) (b, c) = (c, b);
        mesh.TriangleIndices.Add(a);
        mesh.TriangleIndices.Add(b);
        mesh.TriangleIndices.Add(c);
    }

    private static Vector3D N3(Vector n) => new(n.X, n.Y, 0);

    private static Vector3D Normalize(Vector3D v)
    {
        if (v.LengthSquared > 0) v.Normalize();
        return v;
    }
}
