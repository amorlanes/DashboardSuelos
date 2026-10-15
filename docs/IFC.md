# Vincular modelos IFC a las partidas

El módulo **Modelo IFC** permite importar mediciones del modelo, relacionar sus
elementos con partidas del proyecto y comparar revisiones sin conexión y sin API
keys. Esta primera fase trabaja con archivos IFC y cantidades declaradas en ellos.
La integración directa con Revit y el visor geométrico 3D son fases posteriores.

## Si tu modelo está en Revit

Abre el `.rvt` en Revit y utiliza **Archivo → Exportar → IFC**. Elige IFC4 o
IFC2x3 y activa la exportación de cantidades base en las opciones del exportador.
Conserva los identificadores IFC entre exportaciones para comparar revisiones del
mismo modelo. Después importa el `.ifc` resultante en esta aplicación; el archivo
`.rvt` no se abre directamente en esta primera fase.

## Primera vinculación

1. Abre un proyecto desde la pantalla principal. Para la prueba portátil puedes
   usar **Vivienda de demostración**, que ya contiene dos partidas.
2. Abre la pestaña **Modelo IFC** y utiliza el botón para vincular un IFC nuevo.
3. Selecciona tu archivo `.ifc`. El importador extrae identificadores, clase,
   nombre, tipo y cantidades. Revisa los avisos del archivo.
4. Carga la versión importada y selecciona el elemento que quieras asociar.
5. Elige la partida, una regla de medición y el factor. Por ejemplo, vincula un
   muro a **CER-01**, con **Área neta** y factor **1**.
6. Guarda el vínculo. La tabla de mediciones muestra la cantidad, unidad, precio
   unitario del catálogo y valoración de referencia.

Un elemento puede tener varios vínculos a partidas distintas: por ejemplo,
albañilería medida en m³ y pintura medida en m². Cada pareja elemento/partida tiene
una regla; volver a vincularla cambia esa regla y su factor. El factor permite
representar dos caras de un cerramiento si el área de origen corresponde a una sola
cara; revisa antes si ya incluye ambas. La unidad debe ser compatible con la regla.

## Revisiones del mismo modelo

Selecciona el modelo existente y utiliza la importación de revisión. La aplicación
conserva las versiones anteriores y el IFC original de cada una. Importar de nuevo
el mismo contenido dentro del mismo modelo reutiliza su versión.

Elige una versión anterior y una nueva para comparar altas, bajas y modificaciones.
Los vínculos se conservan por identificador del elemento dentro de su modelo.
Los elementos nuevos requieren revisar su asociación a partidas. Una asociación
de un elemento eliminado se conserva para poder valorar su baja.

El impacto económico usa las reglas vinculadas y los precios actuales del
catálogo. Cuando faltan cantidades o vínculos, se muestra la parte conocida y los
elementos sin valorar. Esa cantidad parcial no representa el coste completo del
cambio.

La comparación utiliza identificadores del IFC. Si el programa de origen los
regenera al exportar, el cambio puede aparecer como numerosas altas y bajas;
revisa los avisos y configura la exportación para conservarlos.

## Probar con los modelos incluidos

El paquete de Windows incluye **Modelos-ejemplo/Modelo-v1.ifc** y
**Modelos-ejemplo/Modelo-v2.ifc**. En el repositorio están en `samples/IFC`.

1. Importa `Modelo-v1.ifc` como modelo nuevo.
2. Vincula el muro **M-01** a **EST-01**, regla **Volumen neto**, factor 1.
3. Vincula **M-02** a **CER-01**, regla **Área neta**, factor 1.
4. Importa `Modelo-v2.ifc` como revisión del mismo modelo.
5. Vincula el elemento nuevo **M-03** a **CER-01**, regla **Área neta**, factor 1.
6. Compara ambas versiones: un añadido, un eliminado y un modificado.

Con los precios del catálogo de demostración (150 €/m³ y 85 €/m²), y los tres
vínculos anteriores, el impacto estimado es **+960 €**: +450 € de hormigón y
+510 € de cerramiento. Antes de vincular M-03 hay una parte pendiente de valorar.

## Cantidades, historial y alcance

Las cantidades se normalizan a m, m² y m³. Se distinguen mediciones netas y brutas.
Una cantidad que no esté declarada en el IFC se muestra como ausente; no se
sustituye por cero ni se deduce de la geometría. La regla **Unidades** cuenta un
elemento como una unidad cuando no hay un recuento explícito; un recuento inválido
o ambiguo sigue figurando como ausente. Revisa las opciones de exportación de
cantidades en la aplicación que produce el IFC.

El lector admite archivos STEP `.ifc` sin comprimir, de hasta **256 MiB**, con un
único `IfcProject`, en los esquemas **IFC2X3, IFC4 e IFC4X1**. IFC4X3, IFCZIP e
IFCXML no están soportados en esta versión. Extrae las cantidades reconocidas de
los conjuntos de medición del archivo; no reconstruye la geometría para calcular
otras mediciones.

Los originales y el historial BIM se guardan localmente junto a los datos de la
aplicación, en `bim.db` y `ModelosBim`. Se puede exportar una copia del IFC guardado
para comprobar qué archivo dio origen a una versión. Su SHA-256 permite detectar
alteraciones de la copia archivada.

En esta fase el historial BIM y sus asociaciones son locales. La sincronización
de certificaciones conserva su funcionamiento; compartir los modelos y los
vínculos BIM entre equipos necesita la siguiente ampliación del servidor.

El técnico confirma la cantidad realmente ejecutada antes de certificarla. La
importación y comparación de IFC no cambian automáticamente las cantidades del
período, las aprobaciones ni las certificaciones facturadas.
