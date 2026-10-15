# Pruebas de integración

```bash
dotnet test tests/Constructora.Integration.Tests/Constructora.Integration.Tests.csproj
```

Requieren PostgreSQL real. Por defecto conectan al servicio local de desarrollo en
`127.0.0.1:54329`, usuario `constructora`. `CONSTRUCTORA_TEST_POSTGRES` permite
configurar otra conexión mediante una variable de entorno; los valores de acceso
no deben guardarse en el repositorio.

La cuenta necesita permiso para crear bases de datos y acceder a `postgres`.
El fixture crea una base `constructora_test_<guid>`, utiliza exclusivamente esa
base para inicializar el esquema y ejecutar la API, y la elimina al finalizar.
No modifica el esquema ni los datos de la base indicada en la conexión original.
Si PostgreSQL no está disponible, las pruebas fallan; no se omiten.

La suite usa `WebApplicationFactory`, EF Core con Npgsql y un cliente SignalR
con transporte LongPolling sobre TestServer. Comprueba persistencia, catálogos,
validación, cambios de líneas, concurrencia `xmin`, reintentos idempotentes,
carreras entre solicitudes y notificaciones emitidas después del commit.

También ejecuta el cliente real `Constructora.Client.Core`, registrado con
`AddConstructoraClient`, contra la API y PostgreSQL. Usa archivos SQLite
temporales independientes para comprobar la descarga de catálogo, el outbox,
la sincronización y un conflicto entre dos equipos con resolución explícita.
Los archivos SQLite se eliminan al finalizar cada prueba.
