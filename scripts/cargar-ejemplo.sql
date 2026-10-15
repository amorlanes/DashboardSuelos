-- Datos de demostración explícitos; este archivo no se ejecuta al arrancar.
INSERT INTO "Proyectos" ("Id", "Nombre", "Codigo", "FechaInicio")
VALUES ('11111111-1111-1111-1111-111111111111', 'Vivienda de demostración', 'DEMO-01', '2026-01-01')
ON CONFLICT ("Id") DO NOTHING;
INSERT INTO "Partidas" ("Id", "ProyectoId", "Codigo", "Descripcion", "Unidad", "PrecioUnitario", "CantidadPrevista")
VALUES
('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'EST-01', 'Hormigón estructural', 'm3', 150, 120),
('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'CER-01', 'Cerramiento', 'm2', 85, 250)
ON CONFLICT ("Id") DO NOTHING;
