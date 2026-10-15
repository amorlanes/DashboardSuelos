# Constructora: prueba portátil para Windows

Descarga **Constructora-Windows-portatil.zip** de los archivos de esta publicación.

1. En Windows 10 u 11 de 64 bits, pulsa **Extraer todo** sobre el ZIP.
2. Abre la carpeta extraída y haz doble clic en **Probar.cmd**.
3. Selecciona **Vivienda de demostración**, pulsa **Abrir proyecto** y después **Nueva certificación**.
4. Edita cantidades y pulsa **Guardar en este equipo**.

El paquete incluye .NET y WPF. Para esta prueba local no necesitas instalar .NET,
Docker ni PostgreSQL. Conserva todos los archivos y la carpeta **Preparador** juntos.

Es normal que aparezca **Sin conexión** si no hay servidor configurado. Puedes
guardar y volver a abrir las certificaciones en tu equipo. La sincronización entre
equipos necesita una API accesible y PostgreSQL en el servidor.

Los datos se guardan en `%LOCALAPPDATA%\Constructora\Certificaciones`. El preparador
añade un catálogo de demostración sin reemplazar registros existentes.

Esta es una versión de prueba. La compilación y las pruebas automatizadas están
verificadas; la interacción visual debe comprobarse en un equipo Windows.
