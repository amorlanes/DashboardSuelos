# Traslado a DashboardSuelos

Trabajo del 22 de octubre de 2026. Destino: https://github.com/amorlanes/DashboardSuelos.

Se conserva la aplicación completa y el historial del checkout de
`amorlanes/Certificaciones-Constructora`, hasta el commit
`364e02c9cfb731e6de4b8c68bca32b773f4a9a40`. El código, los tests y los
lockfiles se han conservado sin cambios. El trabajo de hoy consistió en preparar
y validar el entorno de desarrollo; no se implementaron nuevas funciones.

## Configuración trasladada

- `install.sh`: instalación reproducible desde `/workspace/DashboardSuelos`.
- `start.md`: instrucciones completas de activación del SDK, arranque de
  PostgreSQL y API, comprobación de disponibilidad y pruebas.
- `environment-config.json`: los campos `install_script` y `start_skill`
  exportados del borrador del entorno original y adaptados a la nueva ruta.

El JSON contiene instrucciones, sin valores de credenciales. El instalador usa
los scripts existentes y requiere Docker. Instala .NET SDK 8.0.426 con verificación
SHA-512, restaura con lockfiles, compila Debug y descarga PostgreSQL 16 por digest.
Las dependencias y los datos se preparan bajo `/workspace`, fuera del checkout.

## Usarlo en otro entorno cloud

1. Crea o abre el entorno de destino en la interfaz del producto y selecciona
   `amorlanes/DashboardSuelos`, rama `main`, con checkout en
   `/workspace/DashboardSuelos`.
2. Copia el valor de `install_script` del JSON al script de instalación y el valor
   de `start_skill` a las instrucciones de arranque del entorno. Son los contenidos
   completos; `start_skill` es documentación para el agente, no un script shell.
3. Ejecuta la instalación y las instrucciones de arranque en esa máquina; valida
   `/health`, `/api/proyectos` y `bash scripts/validate.sh`.
4. Revisa y guarda la configuración; después publica el entorno desde el producto.

El texto original del README conserva la ruta del repositorio de origen. Para
este destino, usa `/workspace/DashboardSuelos` y las instrucciones de esta carpeta.
Cada tarea cloud está aislada; no hace falta crear un worktree.

## Comprobaciones realizadas en el checkout de destino

- `bash .cloud/install.sh`: instalación y compilación correctas, sin errores ni
  advertencias, incluido el proyecto WPF mediante EnableWindowsTargeting.
- `bash scripts/validate.sh`: seis suites completadas, 122 pruebas aprobadas,
  sin fallos ni omisiones. Cubren lógica de aplicación, SQLite, IFC/BIM,
  PostgreSQL real, sincronización, concurrencia y SignalR.
- API ejecutada desde `/workspace/DashboardSuelos/src/Constructora.Server`:
  `/health` devuelve HTTP 200 con `{"estado":"Disponible"}`;
  `/api/proyectos` devuelve HTTP 200 con `[]`.
- La base de desarrollo no tiene proyectos, partidas, certificaciones ni líneas;
  el esquema vacío se reproduce con las instrucciones de arranque. Las pruebas
  de integración usan bases temporales independientes y las eliminan.

La validación se realizó en la máquina actual, usando el SDK, la caché de paquetes
y PostgreSQL ya instalados. Los procesos, contenedores, cachés y datos de máquina
no se almacenan en Git: los scripts preparan una máquina nueva. Crear, publicar
y restaurar otro entorno cloud siguen siendo operaciones de la interfaz del
producto y no se han verificado en una segunda máquina.

## Alcance de Windows y publicaciones

La ventana WPF requiere Windows; no se ha ejecutado visualmente en Linux.
Los ZIP y las Releases de GitHub no se trasladan con el historial Git. No se
generó ni publicó ningún paquete Windows durante este trabajo.
Los workflows heredados conservan sus restricciones al repositorio
`amorlanes/Certificaciones-Constructora`; por ello sus trabajos de publicación
no se ejecutarán en DashboardSuelos hasta adaptar esas restricciones. Esto no
afecta a la instalación, al arranque de desarrollo ni a las seis suites probadas.
