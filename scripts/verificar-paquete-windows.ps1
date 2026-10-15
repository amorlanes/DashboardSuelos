[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Esta comprobación ejecuta los binarios nativos de Windows.' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$helper = Join-Path $package 'Preparador/Constructora.Prueba.exe'
$nativeSqlite = Join-Path $package 'Preparador/e_sqlite3.dll'
foreach ($file in @('Constructora.Wpf.exe', 'Probar.cmd', 'LEEME.txt', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Preparador/Constructora.Prueba.exe', 'Preparador/hostfxr.dll', 'Preparador/coreclr.dll', 'Preparador/e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf)) { throw "Falta un archivo del paquete: $file" }
}

# Query the exact native SQLite binary shipped beside the helper, without a CLI download.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConstructoraPortableSqliteProbe
{
    private const string Library = "constructora_portable_sqlite_probe";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Callback(IntPtr state, int count, IntPtr values, IntPtr names);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, Callback callback, IntPtr state, out IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr memory);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);
    public static void Load(string path)
    {
        var handle = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(ConstructoraPortableSqliteProbe).Assembly,
            (name, assembly, searchPath) => name == Library ? handle : IntPtr.Zero);
    }
    public static string Execute(string path, string sql)
    {
        IntPtr database;
        int opened = sqlite3_open_v2(path, out database, 2, IntPtr.Zero);
        if (opened != 0)
        {
            if (database != IntPtr.Zero) sqlite3_close_v2(database);
            throw new InvalidOperationException("No se pudo abrir SQLite, código " + opened);
        }
        try
        {
            string result = "";
            Callback callback = (state, count, values, names) => {
                if (count > 0) result = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(values)) ?? "";
                return 0;
            };
            IntPtr error;
            int status = sqlite3_exec(database, sql, callback, IntPtr.Zero, out error);
            string message = error == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(error) ?? "";
            if (error != IntPtr.Zero) sqlite3_free(error);
            GC.KeepAlive(callback);
            if (status != 0) throw new InvalidOperationException("SQLite " + status + ": " + message);
            return result;
        }
        finally { sqlite3_close_v2(database); }
    }
}
'@
[ConstructoraPortableSqliteProbe]::Load($nativeSqlite)
$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('constructora-paquete-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$database = Join-Path $testDirectory 'certificaciones.db'

function Assert-Sqlite([string]$Sql, [string]$Expected) {
    $actual = [ConstructoraPortableSqliteProbe]::Execute($database, $Sql)
    if ($actual -cne $Expected) { throw "Comprobación SQLite fallida. Esperado '$Expected'; obtenido '$actual'. Consulta: $Sql" }
}

try {
    & $helper --prepare-only --data-directory $testDirectory
    if ($LASTEXITCODE -ne 0) { throw 'El primer arranque del preparador falló.' }
    Assert-Sqlite 'PRAGMA integrity_check;' 'ok'
    Assert-Sqlite 'SELECT COUNT(*) FROM Proyectos;' '1'
    Assert-Sqlite 'SELECT COUNT(*) FROM Partidas;' '2'
    Assert-Sqlite 'SELECT COUNT(*) FROM Certificaciones;' '0'
    Assert-Sqlite 'SELECT COUNT(*) FROM OperacionesPendientes;' '0'
    [ConstructoraPortableSqliteProbe]::Execute($database, @'
BEGIN;
UPDATE Proyectos SET Nombre='Proyecto conservado ñ' WHERE Codigo='DEMO-01';
UPDATE Partidas SET Descripcion='Medición conservada', PrecioUnitario='321.125' WHERE Codigo='EST-01';
INSERT INTO Certificaciones (Id,ProyectoId,NumeroCertificacion,Fecha,Estado,RowVersion,RevisionLocal,VersionesEdicionJson,EsPendienteSincronizacion,TieneConflicto)
VALUES ('44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111',7,'2026-10-15',0,0,1,'[]',1,0);
INSERT INTO OperacionesPendientes (OperacionId,CertificacionId,RevisionLocal,PayloadJson,Preparada,Rechazada)
VALUES ('55555555-5555-5555-5555-555555555555','44444444-4444-4444-4444-444444444444',1,'{"conservada":true}',0,0);
COMMIT;
'@) | Out-Null
    & $helper --prepare-only --data-directory $testDirectory
    if ($LASTEXITCODE -ne 0) { throw 'El segundo arranque del preparador falló.' }
    Assert-Sqlite 'PRAGMA integrity_check;' 'ok'
    Assert-Sqlite 'SELECT COUNT(*) FROM Proyectos;' '1'
    Assert-Sqlite 'SELECT COUNT(*) FROM Partidas;' '2'
    Assert-Sqlite "SELECT Nombre FROM Proyectos WHERE Codigo='DEMO-01';" 'Proyecto conservado ñ'
    Assert-Sqlite "SELECT Descripcion FROM Partidas WHERE Codigo='EST-01';" 'Medición conservada'
    Assert-Sqlite "SELECT PrecioUnitario FROM Partidas WHERE Codigo='EST-01';" '321.125'
    Assert-Sqlite 'SELECT COUNT(*) FROM Certificaciones WHERE NumeroCertificacion=7 AND RevisionLocal=1 AND EsPendienteSincronizacion=1;' '1'
    Assert-Sqlite 'SELECT COUNT(*) FROM OperacionesPendientes WHERE RevisionLocal=1 AND Preparada=0 AND Rechazada=0;' '1'
    Assert-Sqlite 'SELECT PayloadJson FROM OperacionesPendientes;' '{"conservada":true}'
    Write-Host 'Preparador Windows y SQLite nativo comprobados: catálogo inicial, UTF-8, mediciones y cola conservados en el segundo arranque. No se ha abierto la interfaz WPF.'
}
finally {
    Remove-Item -LiteralPath $testDirectory -Recurse -Force
}
