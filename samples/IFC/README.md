# Modelos IFC para probar mediciones

Los archivos `Modelo-v1.ifc` y `Modelo-v2.ifc` son modelos IFC4 STEP con cantidades explícitas y unidades SI: metros, metros cuadrados y metros cúbicos. Están preparados para probar la importación de mediciones, la asociación a partidas y la comparación entre versiones. Los nombres acentuados utilizan escapes STEP Unicode.

Los archivos no contienen representaciones geométricas. Un visor 3D puede mostrarlos vacíos; para probar geometría debe utilizarse un IFC exportado desde una herramienta de modelado.

Cada muro tiene un conjunto `Qto_WallBaseQuantities` con `NetVolume`, `GrossVolume`, `NetSideArea`, `GrossSideArea` y `Length`. La propiedad `Pset_Constructora.CodigoPartida` propone su partida. Las cantidades netas y brutas son alternativas de medición y no deben sumarse.

| Elemento | GlobalId | Partida | V1: volumen neto / área neta | V2: volumen neto / área neta |
|---|---|---|---|---|
| M-01: hormigón | `1YvctVUKr0kugbFTf53O9L` | `EST-01` | 12 m³ / 40 m² | 15 m³ / 50 m² |
| M-02: cerámico | `2YvctVUKr0kugbFTf53O9M` | `CER-01` | 4,5 m³ / 30 m² | Eliminado |
| M-03: cerámico | `3YvctVUKr0kugbFTf53O9N` | `CER-01` | Ausente | 5,4 m³ / 36 m² |

Para una prueba reproducible, crea las partidas `EST-01` con unidad m³ y precio 150 €/m³, y `CER-01` con unidad m² y precio 85 €/m². Importa V1 y asocia M-01 por volumen neto y M-02 por área neta. Después importa V2 en el mismo modelo vinculado y asocia M-03 a CER-01 por área neta. Deben aparecer un elemento modificado, uno eliminado y uno añadido. Aplicando esos criterios, el incremento estimado es de **960 €**: 3 m³ × 150 €/m³ − 30 m² × 85 €/m² + 36 m² × 85 €/m².

El identificador del proyecto y el del muro M-01 se conservan entre versiones para verificar el vínculo estable. Las mediciones del modelo representan cantidades previstas; el técnico debe validar por separado las cantidades ejecutadas que se incluyen en una certificación.
