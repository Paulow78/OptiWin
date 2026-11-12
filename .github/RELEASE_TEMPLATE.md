**Téléchargez `OptiWin.exe` ci-dessous** : un seul fichier, sans installation, pour Windows 10 et 11.

## Au premier lancement
Windows SmartScreen affichera sans doute « Windows a protégé votre ordinateur », car le programme n'est pas signé numériquement. Cliquez sur **Informations complémentaires**, puis sur **Exécuter quand même**, et acceptez la demande d'administrateur.

Pour vérifier le fichier, comparez son empreinte avec `SHA256SUMS.txt` :

```
certutil -hashfile OptiWin.exe SHA256
```

Empreinte attendue : `{{SHA256}}`

## Dans cette version
{{CHANGES}}

## Provenance
Cet exécutable a été compilé et auto-testé par GitHub Actions à partir du tag `v{{VERSION}}`, avec le compilateur C# fourni avec Windows (`build.ps1`) : vous pouvez le reproduire à l'identique depuis le code source.

Logiciel fourni tel quel, sans garantie : sauvegardez vos données avant de modifier votre système ou d'effacer une clé USB.
