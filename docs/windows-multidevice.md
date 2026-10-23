# Installation Sefirah 3.1.0.2 — Windows x64

Copier ce dossier complet sur chaque PC (Windows 10 2004 ou plus récent, ou Windows 11, x64).
Fermer Sefirah avant la mise à jour, puis lancer `Install.ps1` avec PowerShell.
L'assistant Windows peut demander une élévation pour installer le certificat de signature local `Sefirah.cer`.
Si PowerShell bloque le script, ouvrir PowerShell dans ce dossier et exécuter :

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

Le certificat est auto-signé : il faut l'approuver sur chacun de vos PC avant de pouvoir installer le MSIX.
La clé privée ne fait pas partie de ce dossier et ne doit pas être distribuée.

Sur chaque PC :
1. Installer cette même version et ouvrir Sefirah.
2. Autoriser Sefirah sur le réseau privé dans le pare-feu Windows, si demandé.
3. Ouvrir les appareils disponibles, sélectionner l'autre PC, vérifier le même code et accepter l'association.
4. Répéter pour les autres PC et téléphones. Chaque association est directe ; aucun appareil ne relaie le presse-papiers d'un autre.
5. Dans les réglages presse-papiers de chaque appareil, activer l'envoi, la réception et les images.
   Les images sont activées par défaut pour les réglages encore non définis ; les choix existants sont conservés.
6. Copier un texte ou une capture d'écran sur l'un des appareils, puis coller sur un autre appareil associé et connecté.

Les PC annoncent désormais leur type Windows. Les anciens PC déjà associés seront identifiés correctement après leur mise à jour et leur reconnexion.
Android conserve le protocole existant ; les restrictions d'accès au presse-papiers propres à Android restent applicables.

Validation : build Windows sans erreur ; tests du protocole, des caractères Unicode fragmentés, de trois connexions TCP et de l'intégrité d'un contenu image supérieur à 2 Mo.
Le collage dans les applications et l'association sur plusieurs machines physiques restent à vérifier sur vos appareils.
