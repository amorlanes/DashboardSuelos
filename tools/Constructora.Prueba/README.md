# Prueba portátil en Windows

El paquete portátil funciona en Windows 10 u 11 de 64 bits. Incluye el runtime de .NET y SQLite; para esta prueba local no necesitas instalar .NET, Docker ni PostgreSQL.

1. Extrae **todo** el ZIP conservando sus archivos y la carpeta **Preparador**.
2. Haz doble clic en `Probar.cmd`.
3. Selecciona el proyecto **Vivienda de demostración**, pulsa **Abrir proyecto** y **Nueva certificación**, modifica sus cantidades y pulsa **Guardar en este equipo**.

La prueba funciona sin conexión al servidor. El catálogo incluye dos partidas y no crea certificaciones automáticamente. Los cambios quedan en SQLite y pendientes de sincronización. La sincronización entre equipos necesita posteriormente la API y PostgreSQL en funcionamiento.

Los datos se guardan en `%LOCALAPPDATA%\Constructora\Certificaciones\certificaciones.db`, la misma ubicación que usa la aplicación. El preparador añade únicamente datos de demostración ausentes; conserva los registros existentes y no migra esquemas. Si un código de demostración ya pertenece a otro registro, lo conserva sin sustituirlo. Para volver a abrir tus datos, ejecuta otra vez `Probar.cmd`.

Si aparece un error, la ventana de consola permanece abierta para poder copiar el mensaje. Mantén todos los archivos y la carpeta **Preparador** juntos. La aplicación registra sus mensajes en `%LOCALAPPDATA%\Constructora\Certificaciones\cliente.log`.

El paquete se compila en Linux para Windows. El preparador SQLite se ha comprobado en Linux; la ventana WPF y el inicio de los ejecutables de Windows requieren validación en Windows.

## Preparación sin abrir la ventana

`Preparador\Constructora.Prueba.exe --help` muestra las opciones. `--prepare-only` prepara SQLite sin abrir WPF. Puedes indicar una carpeta de prueba con `--prepare-only --data-directory "C:\ruta\prueba"`; esta opción no cambia la carpeta que utiliza WPF.

Para desarrollar o validar el preparador en Linux con el SDK .NET 8:

```bash
dotnet run --project tools/Constructora.Prueba -- --prepare-only --data-directory /tmp/constructora-prueba
```
