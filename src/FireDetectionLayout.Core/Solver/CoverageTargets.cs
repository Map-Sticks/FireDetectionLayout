using System;
using System.Collections.Generic;
using FireDetectionLayout.Core.Models;
using NetTopologySuite.Geometries;

namespace FireDetectionLayout.Core.Solver
{
    /// <summary>
    /// Samples a space into ceiling patches on the room grid. Each patch records how far it
    /// extends towards the walls, so wall reach is measured to the wall face rather than to the
    /// sample point.
    /// </summary>
    internal static class CoverageTargets
    {
        public static List<CoverageTarget> Build(CompartmentContext ctx, double stepMm, double edgeInsetMm)
        {
            var frame = ctx.Frame;
            int nu = Math.Max(1, (int)Math.Ceiling(frame.SpanU / stepMm));
            int nv = Math.Max(1, (int)Math.Ceiling(frame.SpanV / stepMm));
            double du = frame.SpanU / nu;
            double dv = frame.SpanV / nv;

            // Patches hard against a wall are represented by their inboard neighbour, so tiny
            // jogs and column notches do not demand their own device.
            var cells = Sample(ctx, nu, nv, du, dv, edgeInsetMm);
            if (cells == null)
                cells = Sample(ctx, nu, nv, du, dv, 0.0);

            var targets = new List<CoverageTarget>();
            if (cells != null)
            {
                for (int iu = 0; iu < nu; iu++)
                {
                    for (int iv = 0; iv < nv; iv++)
                    {
                        var target = cells[iu, iv];
                        if (target == null)
                            continue;

                        target.ExtentUPlus = iu + 1 < nu && cells[iu + 1, iv] != null
                            ? du * 0.5
                            : RayExtent(ctx, target.Point, frame.AxisU, 1.0, du);
                        target.ExtentUMinus = iu > 0 && cells[iu - 1, iv] != null
                            ? du * 0.5
                            : RayExtent(ctx, target.Point, frame.AxisU, -1.0, du);
                        target.ExtentVPlus = iv + 1 < nv && cells[iu, iv + 1] != null
                            ? dv * 0.5
                            : RayExtent(ctx, target.Point, frame.AxisV, 1.0, dv);
                        target.ExtentVMinus = iv > 0 && cells[iu, iv - 1] != null
                            ? dv * 0.5
                            : RayExtent(ctx, target.Point, frame.AxisV, -1.0, dv);
                        targets.Add(target);
                    }
                }
            }

            var interior = ctx.Geometry.InteriorPoint;
            var interiorPoint = new Point2D(interior.X, interior.Y);
            targets.Add(new CoverageTarget
            {
                Point = interiorPoint,
                U = frame.U(interiorPoint),
                V = frame.V(interiorPoint)
            });
            return targets;
        }

        private static CoverageTarget?[,]? Sample(
            CompartmentContext ctx, int nu, int nv, double du, double dv, double edgeInsetMm)
        {
            var frame = ctx.Frame;
            var cells = new CoverageTarget?[nu, nv];
            bool any = false;
            for (int iu = 0; iu < nu; iu++)
            {
                double u = frame.MinU + (iu + 0.5) * du;
                for (int iv = 0; iv < nv; iv++)
                {
                    double v = frame.MinV + (iv + 0.5) * dv;
                    var point = frame.ToWorld(u, v);
                    if (!ctx.Geometry.Contains(CompartmentContext.ToPoint(point)))
                        continue;
                    if (edgeInsetMm > 0.0 && ctx.BoundaryDistance(point) < edgeInsetMm)
                        continue;

                    cells[iu, iv] = new CoverageTarget { Point = point, U = u, V = v };
                    any = true;
                }
            }

            return any ? cells : null;
        }

        /// <summary>
        /// Distance from a patch centre to the wall in one direction, capped at one and a half
        /// cells (the patch plus an unsampled neighbour).
        /// </summary>
        private static double RayExtent(CompartmentContext ctx, Point2D origin, Point2D axis, double sign, double cellMm)
        {
            // Solved directly rather than with a geometry overlay, which can fail on the
            // near-coincident coordinates of a rotated room.
            double length = cellMm * 1.5;
            double dirX = axis.X * sign;
            double dirY = axis.Y * sign;
            double nearest = length;

            void Scan(Coordinate[] ring)
            {
                for (int i = 0; i + 1 < ring.Length; i++)
                {
                    double edgeX = ring[i + 1].X - ring[i].X;
                    double edgeY = ring[i + 1].Y - ring[i].Y;
                    double denominator = dirX * edgeY - dirY * edgeX;
                    if (Math.Abs(denominator) < 1e-12)
                        continue;

                    double offsetX = ring[i].X - origin.X;
                    double offsetY = ring[i].Y - origin.Y;
                    double alongRay = (offsetX * edgeY - offsetY * edgeX) / denominator;
                    double alongEdge = (offsetX * dirY - offsetY * dirX) / denominator;
                    if (alongRay > 1e-6 && alongRay < nearest && alongEdge >= -1e-9 && alongEdge <= 1.0 + 1e-9)
                        nearest = alongRay;
                }
            }

            Scan(ctx.Visibility.ExteriorRing.Coordinates);
            for (int i = 0; i < ctx.Visibility.NumInteriorRings; i++)
                Scan(ctx.Visibility.GetInteriorRingN(i).Coordinates);
            return nearest;
        }
    }
}
