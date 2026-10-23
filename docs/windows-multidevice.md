# Installation Sefirah 3.1.0.4 — Windows x64

Copier ce dossier complet sur chaque PC (Windows 10 2004 ou plus récent, ou Windows 11, x64).
Fermer Sefirah avant la mise à jour, puis lancer `Install.ps1` avec PowerShell.
Le MSIX utilise le certificat historique `Sefirah-SigningCert.cer` (empreinte `129CC6F085FB2E35AAB7CB927030257FB15A8F27`). Aucune nouvelle approbation de certificat n'est nécessaire sur les PC où ce certificat est déjà approuvé.
Si PowerShell bloque le script, ouvrir PowerShell dans ce dossier et exécuter :

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

Sur un nouveau PC uniquement, approuver le certificat historique fourni avant d'installer le MSIX. Sur les PC déjà configurés, un simple `Add-AppxPackage -Path .\Sefirah_3.1.0.4_x64_Debug.msix` permet la mise à jour.
La clé privée ne fait pas partie de ce dossier et ne doit pas être distribuée.

Sur chaque PC :
1. Installer cette même version et ouvrir Sefirah.
2. Autoriser Sefirah sur le réseau privé dans le pare-feu Windows, si demandé.
3. Ouvrir les appareils disponibles, sélectionner l'autre PC, vérifier le même code et accepter l'association.
4. Répéter pour les autres PC et téléphones. Chaque association est directe ; aucun appareil ne relaie le presse-papiers d'un autre.
5. Dans les réglages presse-papiers de chaque appareil, activer l'envoi, la réception et les images.
   Les images sont activées par défaut pour les réglages encore non définis ; les choix existants sont conservés.
6. Les appareils joignables se reconnectent automatiquement. Le bouton de reconnexion (flèche circulaire) dans la barre latérale permet de relancer toutes les connexions immédiatement.
7. Vérifier les états individuels et le compteur « appareils connectés » dans le panneau principal.
8. Copier un texte ou une capture d'écran sur l'un des appareils, puis coller sur un autre appareil associé et connecté.

« Voir les détails » choisit seulement l'appareil dont les détails sont affichés. Les autres appareils restent connectés et continuent de partager le presse-papiers selon leurs réglages. Une reconnexion ne change plus l'appareil affiché. Le bouton « Déconnecter » agit uniquement sur l'appareil concerné.

Les PC annoncent désormais leur type Windows. Les anciens PC déjà associés seront identifiés correctement après leur mise à jour et leur reconnexion.
Android conserve le protocole existant ; les restrictions d'accès au presse-papiers propres à Android restent applicables.

Validation : build Windows sans erreur ; tests du changement de détails sans déconnexion, de la connexion simultanée et du compteur d’appareils ; tests du protocole, des caractères Unicode fragmentés, de trois connexions TCP et de l'intégrité d'un contenu image supérieur à 2 Mo.
Le collage dans les applications et l'association sur plusieurs machines physiques restent à vérifier sur vos appareils.


Évolutions de la version 3.1.0.4 :
- Compteur partagé et actualisé après le chargement des appareils, les associations, les connexions et les déconnexions. Deux appareils connectés sur trois connus donnent « 2 / 3 » dans la barre latérale.
- Liste compacte dans la barre latérale, actions secondaires dans le menu « … », détails regroupés dans un volet dépliable.
- Cartes d'appareils allégées, états signalés par un point vert (connecté), orange (connexion en cours) ou gris (hors ligne).
- Le certificat de signature historique est conservé.
