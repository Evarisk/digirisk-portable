<div align="center">
  <img src="App/DigiRisk/img/logo.png" alt="Logo DigiRisk" width="96" height="96" />
  <h1>DigiRisk Portable</h1>
  <p><strong>Application légère de saisie terrain de risques, photos, cotations et tâches pour DigiRisk (Dolibarr) au format officiel PortableApps.com (PAF 3.9)</strong></p>

  <p>
    <a href="https://portableapps.com/development/portableapps.com_format"><img src="https://img.shields.io/badge/Format-PortableApps.com%203.9-2563eb" alt="PAF 3.9" /></a>
    <img src="https://img.shields.io/badge/Windows-Sans%20droits%20Admin%20(asInvoker)-10b981" alt="Zero Admin" />
    <a href="https://github.com/Evarisk/DigiRisk"><img src="https://img.shields.io/badge/Dolibarr-DigiRisk%20API-f59e0b" alt="DigiRisk API" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License GPLv3" /></a>
  </p>

  <p>
    <a href="https://linktr.ee/digirisk">🌐 Linktree DigiRisk</a> •
    <a href="https://github.com/Evarisk/DigiRisk">📦 Dépôt DigiRisk Dolibarr</a> •
    <a href="https://evarisk.com">🏢 Evarisk</a> •
    <a href="help.html">📖 Aide locale</a>
  </p>
</div>

---

## 🎯 Pourquoi ce projet ?

Sur le terrain ou en entreprise, les utilisateurs chargés de alimenter le **Document Unique d'Évaluation des Risques Professionnels (DUERP)** utilisent souvent des postes Windows verrouillés :
- **Aucun droit Administrateur local** (impossible d'installer un logiciel dans `C:\Program Files` ou d'écrire dans le registre Windows).
- **Blocage des scripts par GPO** (les fichiers `.bat`, `.cmd`, `.vbs` ou `.ps1` sont bloqués par les politiques de sécurité système).

**DigiRisk Portable** répond à cette contrainte en proposant une application conforme à la spécification **PortableApps.com Format (PAF 3.9)** :
- Un exécutable natif Windows léger (`DigiRiskPortable.exe`, `~24 Ko`) compilé avec un manifeste `requestedExecutionLevel="asInvoker"` (`RunAsAdmin=none`).
- **Zéro installation** : fonctionne directement depuis une clé USB, un dossier utilisateur, OneDrive, un partage réseau UNC ou le menu **PortableApps.com Platform**.
- **100 % autonome** : s'appuie uniquement sur les composants natifs déjà présents sur Windows 10/11 (`.NET Framework 4.x` et `Edge`/`Chrome` en mode `--app`).

---

## ✨ Fonctionnalités principales

### 1. 🔗 Gestion Multi-Sessions & MultiCompany
- **Sessions multiples** : enregistrez plusieurs serveurs Dolibarr / DigiRisk (Production, Pré-prod, Démo, Local) et basculez d'une session à l'autre en un clic depuis l'en-tête.
- **Authentification souple** : saisie directe de la clé `DOLAPIKEY` ou récupération automatique du jeton via votre identifiant et mot de passe Dolibarr (`GET /login`).
- **Diagnostic d'installation Dolibarr (`/` vs `/htdocs`)** : bouton de test intégré vérifiant automatiquement si l'API répond sur `/api/index.php` ou `/htdocs/api/index.php` ainsi que la disponibilité des routes DigiRisk.
- **Support MultiCompany complet** :
  - Récupération automatique des entités (`GET /multicompany`) ou configuration manuelle (`id:Nom`).
  - Changement d'entité en direct dans l'en-tête (`DOLENTITY` / `DOLAPIENTITY`) avec **rechargement automatique de la liste des UT/GP** et du **Projet DU** propre à l'entité sélectionnée.
  - Affichage hiérarchique indenté (`└─`) des Groupements (`[GP]`) et Unités de Travail (`[UT]`) selon leur parenté (`fk_parent`).

### 2. ⚠️ Identification du Risque
- Grille interactive embarquant les **30 pictogrammes officiels de catégories de dangers DigiRisk** (disponibles même hors-ligne).
- Pré-remplissage intelligent et personnalisation de la description du risque.

### 3. 📊 Cotation Simple (`standard`) & Avancée (`advanced`)
- **Évaluation simple (`method: "standard"`)** : sélection rapide parmi les 4 seuils officiels DigiRisk :
  - `0-47` (Faible — Gris)
  - `48-50` (Modéré — Orange)
  - `51-80` (Élevé — Rouge)
  - `81-100` (Critique — Noir)
- **Évaluation avancée (`method: "advanced"`)** :
  - Grille officielle **5×5** des critères Evarisk (**Gravité**, **Exposition**, **Occurrence**, **Formation**, **Protection**) graduée de `0` à `4`.
  - Calcul temps réel du score selon la matrice officielle Evarisk avec affichage du badge **Évaluation calculée\***.
- Commentaire d'évaluation associé.

### 4. 📸 Multi-Upload de Photos (Caméra & Galerie)
- **Galerie de fichiers (`multiple`)** : sélectionnez plusieurs images simultanément ou ajoutez-en en plusieurs fois sans écraser les précédentes.
- **Capture Webcam en rafale** : prenez plusieurs photos à la suite via le bouton **📸 Capturer + Continuer** ou validez directement avec **✅ Capturer & Terminer**.
- **Photo principale (`★`)** : choisissez via l'étoile quelle photo illustrera principalement l'évaluation (`$assessment->photo`) dans DigiRisk, toutes les photos étant envoyées dans le dossier `riskassessment/RAxx/` avec génération automatique des miniatures Saturne (`mini`, `small`, `medium`, `large`).
- **Optimisation automatique** : redimensionnement (`max 1920px`) et compression JPEG côté client (`< 5 Mo`) avant envoi.
- **Vignettes compactes** : aperçu miniature, étoile de favori, suppression unitaire (`✕`) et affichage du nom du fichier au survol (`title`).

### 5. ✅ Bloc Tâche Associé
- Création simultanée d'une tâche Dolibarr reliée au risque créé (`options_fk_risk`) et rattachée au projet DU de l'entité active :
  - **Libellé** de la tâche
  - **Date de début** (initialisée à la date/heure courante) et **Date de fin**
  - **Responsable** (alimenté depuis les utilisateurs Dolibarr `GET /users`)
  - **Budget** prévisionnel (`€`)

### 6. 📋 Mode Hors-Ligne & File d'Attente
- En cas d'absence de connexion réseau ou d'erreur serveur, le risque (avec toutes ses photos, sa cotation et sa tâche) est sauvegardé localement dans `Data/settings/queue.json`.
- Modale dédiée **En attente** permettant de :
  - Visualiser les risques en attente (pictogramme, miniature avec compteur `+N` photos, cotation, tâche, date).
  - **Recharger (`✎ Éditer`)** un risque dans le formulaire principal pour le compléter ou le modifier.
  - **Envoyer unitairement** ou **Tout synchroniser** dès le retour de la connexion.
  - **Exporter en JSON** pour archivage ou transfert.

---

## 📁 Structure du Projet (PortableApps.com Format 3.9)

```text
DigiRiskPortable/
├── DigiRiskPortable.exe              # Lanceur principal PAF (sans droits admin, asInvoker)
├── help.html                         # Documentation utilisateur locale conforme PAF
├── README.md                         # Documentation du dépôt
├── AGENTS.md                         # Instructions techniques pour les agents IA / contributeurs
├── LICENSE                           # Licence GPLv3
├── App/
│   ├── AppInfo/
│   │   ├── appinfo.ini               # Métadonnées officielles PortableApps.com Format 3.9
│   │   ├── appicon.ico               # Icône multi-résolutions Windows
│   │   ├── appicon_16.png            # Icône 16x16
│   │   ├── appicon_32.png            # Icône 32x32
│   │   ├── appicon_128.png           # Icône 128x128
│   │   └── Launcher/
│   │       └── DigiRiskPortable.ini  # Configuration du lanceur (RunAsAdmin=none)
│   ├── DefaultData/
│   │   └── settings/
│   │       └── settings.json         # Modèle de configuration vierge copié au 1er lancement
│   └── DigiRisk/
│       ├── DigiRiskApp.exe           # Binaire applicatif interne
│       ├── index.html                # Interface Web DigiRisk autonome (HTML5/CSS3/JS Vanilla)
│       └── img/
│           ├── logo.png              # Logo officiel DigiRisk
│           └── categorieDangers/     # 30 pictogrammes officiels de dangers DigiRisk
├── Data/                             # (Ignoré par Git) Données persistantes de l'utilisateur
│   ├── settings/
│   │   ├── settings.json             # Sessions, clés API et préférences utilisateur
│   │   └── queue.json                # File d'attente hors-ligne (risques + photos + tâches)
│   └── EdgeProfile/                  # Profil navigateur isolé sur le support portable
└── Other/
    └── Source/
        ├── Launcher.cs               # Code source C# du lanceur et serveur proxy local
        ├── app.manifest              # Manifeste UAC Windows (requestedExecutionLevel=asInvoker)
        └── build.ps1                 # Script de compilation reproductible via csc.exe natif
```

---

## 🚀 Démarrage Rapide

### Prérequis côté serveur Dolibarr / DigiRisk
1. Activer le module **API REST** de Dolibarr ainsi que le module **DigiRisk**.
2. Vérifier que l'utilisateur dispose des permissions :
   - `digiriskdolibarr -> api -> read` et `write`
   - `digiriskdolibarr -> risk -> read` et `write`
   - `projet -> creer` (pour l'ajout de tâches)

### Lancement de l'application
1. Double-cliquez sur **`DigiRiskPortable.exe`** (ou lancez **DigiRisk Portable** depuis le menu **PortableApps.com Platform**).
2. Cliquez sur **⚙ Sessions** en haut de l'écran :
   - Donnez un nom à votre session (ex. *DigiRisk Production*).
   - Renseignez l'**URL du serveur** (ex. `https://mon-dolibarr.fr` ou `http://localhost/dolibarr23`).
   - Cliquez au besoin sur **🔍 Tester l'installation (`/` ou `/htdocs`)** pour vérifier automatiquement le chemin d'accès à l'API.
   - Renseignez votre **Clé API (`DOLAPIKEY`)** (ou utilisez le bloc *Récupérer ma clé API avec mon identifiant et mot de passe*).
   - Cliquez sur **Enregistrer & Connecter**.
3. Sélectionnez votre **Entité MultiCompany** et votre **Unité de Travail / Groupement (`Unité / GP*`)** dans la barre supérieure, puis saisissez vos risques !

---

## 🔒 Sécurité & Confidentialité

- **Aucune élévation de privilèges (`asInvoker`)** : l'application s'exécute avec les droits standards de l'utilisateur courant et ne modifie ni le registre Windows ni `%APPDATA%`.
- **Écoute strictement locale (`127.0.0.1`)** : le micro-serveur HTTP intégré n'écoute que sur l'interface de bouclage locale (`IPAddress.Loopback`) sur un port éphémère aléatoire et s'arrête automatiquement dès la fermeture de la fenêtre.
- **Protections HTTP intégrées dans `Launcher.cs`** :
  - **Anti DNS-Rebinding** : validation stricte de l'en-tête `Host` (`127.0.0.1` ou `localhost`).
  - **Anti-CSRF** : validation de l'en-tête `Origin` et obligation de l'en-tête personnalisé `X-DigiRisk-Local: 1` sur toutes les requêtes `POST` (`/local/settings` et `/local/proxy`).
  - **Anti-SSRF** : le relais `/local/proxy` valide que l'URL cible utilise exclusivement les schémas `http://` ou `https://`.
  - **Anti-Path Traversal** : vérification canonique stricte que tout fichier statique servi réside dans `App\DigiRisk\`.
  - **Protection XSS & En-têtes de sécurité** : échappement HTML systématique des données distantes et envoi des en-têtes `X-Content-Type-Options: nosniff` et `X-Frame-Options: DENY`.
- **Isolation des données sensibles** : le dossier `Data/` contenant les jetons `DOLAPIKEY` et la file d'attente locale est exclu du contrôle de version via `.gitignore`.

---

## 🛠️ Compilation depuis les sources

Aucune installation de Visual Studio ou de SDK tiers n'est nécessaire. La compilation utilise directement le compilateur C# natif de Windows (`csc.exe` inclus dans `.NET Framework 4.x`) :

```powershell
powershell -ExecutionPolicy Bypass -File .\Other\Source\build.ps1
```

Cette commande génère et met à jour :
- `DigiRiskPortable.exe` (à la racine)
- `App\DigiRisk\DigiRiskApp.exe`

---

## 🔌 Routes API Dolibarr / DigiRisk utilisées

| Méthode & Route | Usage dans DigiRisk Portable |
| :--- | :--- |
| `GET /status` | Vérification de la disponibilité et de la version de l'API Dolibarr |
| `GET /login` | Récupération de la clé `DOLAPIKEY` à partir des identifiants utilisateur |
| `GET /multicompany` | Chargement dynamique de la liste des entités MultiCompany |
| `GET /digiriskdolibarr/risks/creation-options` | Chargement de l'arborescence active des UT/GP (`elements`), des catégories de dangers (`categories`) et du projet DU (`du_project_id`) de l'entité active |
| `POST /digiriskdolibarr/risks/create-with-photo` | Création atomique d'un risque, de sa cotation (`standard` ou `advanced`) et de sa galerie de photos (`photos[]`) avec génération des miniatures Saturne |
| `POST /tasks` | Création de la tâche associée au risque (`options_fk_risk`) dans le projet DU de l'entité active |
| `GET /users` | Chargement de la liste des collaborateurs pour l'attribution du responsable de tâche |

---

## 📄 Licence

Ce projet est distribué sous licence **GNU General Public License v3.0 (GPLv3)** — voir le fichier [LICENSE](LICENSE) pour plus de détails.
Développé par [Evarisk](https://evarisk.com).
