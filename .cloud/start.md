# Constructora: desarrollo cloud Linux

Trabaja en el checkout existente /workspace/DashboardSuelos. Cada tarea cloud ya está aislada: no crees un Git worktree salvo petición explícita del usuario. Conserva los archivos versionados, manifiestos y lockfiles sin cambios durante el setup.

## Herramientas y archivos persistentes

El instalador ejecuta bash scripts/cloud-install.sh desde el repositorio: instala .NET SDK 8.0.426 con comprobación SHA-512, restaura Constructora.sln con --locked-mode, compila Debug con tres procesos y descarga PostgreSQL 16 por digest. Docker debe estar disponible. Los SDK y paquetes están en /workspace/.dotnet y /workspace/.nuget; los datos PostgreSQL están en /workspace/.constructora/postgres. Para usar dotnet en cada shell:
```bash
cd /workspace/DashboardSuelos
source scripts/dotnet-env.sh
```
Si faltan dependencias o los binarios necesitan actualizarse, ejecuta bash scripts/cloud-install.sh. No desactives verificaciones TLS, firmas ni checksums.

## Servicios que deben arrancarse en cada máquina

Los procesos y contenedores no se conservan con el snapshot. Comprueba primero /health con curl local. Si devuelve HTTP 200 y {"estado":"Disponible"}, reutiliza el servicio y comprueba también /api/proyectos. Si hay un proceso existente que falla, diagnostica el log y el contenedor antes de iniciar otra instancia.

Para arrancar cuando la API no está ejecutándose:
```bash
cd /workspace/DashboardSuelos
mkdir -p /workspace/.constructora
nohup bash scripts/start-dev.sh > /workspace/.constructora/api.log 2>&1 < /dev/null &
printf '%s\n' "$!" > /workspace/.constructora/api.pid
```
start-dev.sh carga dotnet-env.sh, crea o reinicia constructora-postgres y espera pg_isready; después ejecuta la API sin recompilar. Los valores predeterminados no requieren secretos: PostgreSQL local con usuario/base constructora y autenticación trust, publicado exclusivamente en 127.0.0.1:54329; API en 127.0.0.1:5080. Database__Initialize=true inicializa únicamente una base nueva con EnsureCreated; no migra esquemas existentes. No cargues datos de ejemplo sin que la tarea lo requiera. Respeta las variables de conexión existentes; nunca imprimas sus valores ni los guardes aquí.

Espera hasta 45 segundos al arranque y verifica las respuestas, no solo el PID o puerto:
```bash
curl --fail --silent --show-error http://127.0.0.1:5080/health
curl --fail --silent --show-error http://127.0.0.1:5080/api/proyectos
```
/health debe devolver {"estado":"Disponible"} y /api/proyectos un array JSON; una base nueva devuelve []. Si falla, inspecciona /workspace/.constructora/api.log y docker logs --tail 50 constructora-postgres sin exponer secretos. No anuncies disponibilidad si la comprobación no pasa. No generes enlaces de preview a loopback.

## Validación

Desde /workspace/DashboardSuelos ejecuta bash scripts/validate.sh. Arranca/reutiliza PostgreSQL, compila la solución y ejecuta los seis proyectos de pruebas, incluidos SQLite real, IFC/BIM, PostgreSQL y SignalR. Las pruebas de integración crean y eliminan bases constructora_test_<guid>; CONSTRUCTORA_TEST_POSTGRES permite una conexión alternativa con permiso para crear bases. Conserva el código de salida de la validación y comprueba los resultados de cada suite. El onboarding comprobó 122 pruebas aprobadas y compilación sin errores ni advertencias; no asumas ese resultado para cambios futuros.

WPF se compila en Linux mediante EnableWindowsTargeting; ejecutar y validar visualmente su interfaz requiere Windows. La publicación del paquete Windows es opcional y no forma parte del arranque de desarrollo.

Al terminar el setup, comprueba git status --short --untracked-files=all: solo deben quedar los cambios del usuario o salidas locales previstas. Guardar este borrador no publica el entorno ni acredita restauración en una nueva tarea.
