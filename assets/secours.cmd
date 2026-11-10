@echo off
setlocal EnableExtensions EnableDelayedExpansion
title OptiWin - Kit de secours

rem Texte sans accent : l'environnement de reparation n'utilise pas la meme page de codes que Windows.
rem Ce script ne formate rien et ne supprime rien. Chaque reparation demande une confirmation.

if /i "%~1"=="--check" goto check

:menu
call :detect
cls
echo.
echo   ================================================
echo     OptiWin - Kit de secours
echo   ================================================
echo.
if "%WIN%"=="" (
  echo   Aucun Windows trouve sur les disques de ce PC.
) else (
  echo   Windows selectionne : %WIN%:
  if not "%FOUND%"==" %WIN%" echo   Windows trouves    :%FOUND%
)
echo.
echo   1. Copier mes fichiers vers un autre disque
echo   2. Verifier et reparer le disque
echo   3. Reparer les fichiers de Windows
echo   4. Annuler une mise a jour bloquee
echo   5. Reparer le demarrage de Windows
echo   6. Afficher les disques et leurs lettres
echo   7. Choisir un autre Windows
echo   8. Invite de commandes
echo   0. Quitter
echo.
set "CHOIX="
set /p "CHOIX=  Votre choix : "
if "%CHOIX%"=="1" goto backup
if "%CHOIX%"=="2" goto chk
if "%CHOIX%"=="3" goto sfc
if "%CHOIX%"=="4" goto revert
if "%CHOIX%"=="5" goto boot
if "%CHOIX%"=="6" goto volumes
if "%CHOIX%"=="7" goto pick
if "%CHOIX%"=="8" goto shell
if "%CHOIX%"=="0" goto end
goto menu

:detect
set "FOUND="
for %%L in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
  if exist "%%L:\Windows\System32\ntoskrnl.exe" set "FOUND=!FOUND! %%L"
)
if not defined FOUND set "WIN="& goto :eof
if defined WIN (
  echo !FOUND! | find /i "%WIN%" >nul && goto :eof
)
for %%L in (%FOUND%) do if not defined FIRST set "FIRST=%%L"
set "WIN=%FIRST%"
set "FIRST="
goto :eof

:needwin
if "%WIN%"=="" (
  echo.
  echo   Aucun Windows trouve : cette action n'est pas possible.
  set "OK=0"
  goto :eof
)
set "OK=1"
goto :eof

:confirm
echo.
set "R="
set /p "R=  Continuer ? Tapez O pour oui : "
if /i "%R%"=="O" (set "OK=1") else (set "OK=0")
goto :eof

:pause
echo.
set /p "R=  Appuyez sur Entree pour revenir au menu "
goto menu

:pick
echo.
echo   Windows trouves :%FOUND%
set "L="
set /p "L=  Lettre du Windows a utiliser : "
if "%L%"=="" goto menu
if exist "%L%:\Windows\System32\ntoskrnl.exe" (
  set "WIN=%L%"
) else (
  echo   Pas de Windows sur %L%:
  goto pause
)
goto menu

:volumes
echo.
set "DP=%TEMP%\optiwin-vol.txt"
echo list volume> "%DP%"
diskpart /s "%DP%"
goto pause

:backup
call :needwin
if "%OK%"=="0" goto pause
echo.
echo   Copie de %WIN%:\Users vers un autre disque. Les originaux ne sont pas modifies.
echo   Attention : une cle FAT32 refuse les fichiers de plus de 4 Go.
echo.
set "T="
set /p "T=  Lettre du disque de destination : "
if "%T%"=="" goto menu
if /i "%T%"=="%WIN%" (
  echo   La destination doit etre un autre disque.
  goto pause
)
if not exist "%T%:\" (
  echo   Le disque %T%: est introuvable.
  goto pause
)
set "DEST=%T%:\Sauvegarde-OptiWin\Users"
echo.
echo   De   : %WIN%:\Users
echo   Vers : %DEST%
call :confirm
if "%OK%"=="0" goto menu
robocopy "%WIN%:\Users" "%DEST%" /E /XJ /R:1 /W:1 /NP /XD "AppData\Local\Temp" "Temporary Internet Files"
rem Robocopy renvoie un code inferieur a 8 quand la copie s'est bien passee.
if errorlevel 8 (
  echo.
  echo   Certains fichiers n'ont pas pu etre copies : voir ci-dessus.
) else (
  echo.
  echo   Copie terminee dans %DEST%
)
goto pause

:chk
call :needwin
if "%OK%"=="0" goto pause
echo.
echo   Recherche et correction des erreurs du disque %WIN%:
echo   Sur un disque abime, faites d'abord une copie de vos fichiers (choix 1).
call :confirm
if "%OK%"=="0" goto menu
chkdsk %WIN%: /f /x
goto pause

:sfc
call :needwin
if "%OK%"=="0" goto pause
echo.
echo   Verification des fichiers systeme de %WIN%:\Windows (10 a 30 minutes).
call :confirm
if "%OK%"=="0" goto menu
sfc /scannow /offbootdir=%WIN%:\ /offwindir=%WIN%:\Windows
goto pause

:revert
call :needwin
if "%OK%"=="0" goto pause
echo.
echo   A utiliser si Windows ne demarre plus juste apres une mise a jour.
echo   Les mises a jour en attente de %WIN%: seront annulees.
call :confirm
if "%OK%"=="0" goto menu
dism /Image:%WIN%:\ /ScratchDir:%WIN%:\Windows\Temp /Cleanup-Image /RevertPendingActions
goto pause

:boot
call :needwin
if "%OK%"=="0" goto pause
echo.
echo   Recree les fichiers de demarrage a partir de %WIN%:\Windows.
echo   Vos fichiers et vos programmes ne sont pas concernes.
call :confirm
if "%OK%"=="0" goto menu
bcdboot %WIN%:\Windows
if errorlevel 1 (
  echo.
  echo   Echec. Le choix 6 montre les disques : la partition de demarrage est peut-etre abimee.
) else (
  echo.
  echo   Fichiers de demarrage recrees. Retirez la cle et redemarrez.
)
goto pause

:shell
echo.
echo   Tapez EXIT pour revenir au menu.
cmd /k
goto menu

:check
call :detect
echo WINDOWS_TROUVES:%FOUND%
echo WINDOWS_CHOISI:%WIN%
exit /b 0

:end
endlocal
exit /b 0
