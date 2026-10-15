@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0Preparador\Constructora.Prueba.exe" (
  echo No se encuentra Preparador\Constructora.Prueba.exe. Extrae todo el ZIP y conserva sus carpetas.
  pause
  exit /b 1
)
"%~dp0Preparador\Constructora.Prueba.exe" %*
if errorlevel 1 (
  echo.
  echo No se pudo iniciar la prueba. Copia el mensaje anterior para revisar el problema.
  pause
  exit /b 1
)
exit /b 0
