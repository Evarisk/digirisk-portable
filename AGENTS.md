# AGENTS.md — DigiRisk Portable (PortableApps.com Format)

Ce dépôt suit la spécification **PortableApps.com Format (PAF) v3.9** pour fournir un client léger de saisie rapide **DigiRisk** (Dolibarr) utilisable sans droits administrateur sous Windows.

## 1. Emplacement dans l'écosystème PortableApps

Pour être détectée automatiquement par le menu `PortableAppsPlatform.exe`, l'application doit résider dans le dossier racine `PortableApps/` sous le nom de son `AppID` :
- Chemin de développement : `C:\Users\laure\OneDrive\developpement\PortableApps\DigiRiskPortable`
- Plateforme PortableApps.com : `C:\Users\laure\OneDrive\developpement\PortableApps\PortableApps.com`

## 2. Arborescence Standard PAF (PortableApps.com Format)

```text
DigiRiskPortable/
├── AGENTS.md                        # Documentation et règles pour les agents IA
├── DigiRiskPortable.exe             # Lanceur principal PortableApps (sans droits admin, asInvoker)
├── help.html                        # Documentation hors-ligne obligatoire PAF
├── App/
│   ├── AppInfo/
│   │   ├── appinfo.ini              # Métadonnées PAF v3.9 (AppID=DigiRiskPortable)
│   │   ├── appicon.ico              # Icône multi-résolution (16, 32, 48, 256)
│   │   ├── appicon_16.png           # Icône 16x16
│   │   ├── appicon_32.png           # Icône 32x32
│   │   ├── appicon_128.png          # Icône 128x128
│   │   └── Launcher/
│   │       └── DigiRiskPortable.ini # Configuration du lanceur PortableApps
│   ├── DefaultData/
│   │   └── settings/
│   │       └── settings.json        # Configuration par défaut copiée vers Data/ au 1er lancement
│   └── DigiRisk/
│       ├── DigiRiskApp.exe          # Exécutable applicatif (serveur API/proxy + fenêtre WebView/Edge)
│       ├── index.html               # Interface Web autonome (utilisable aussi en direct sans .exe)
│       └── img/
│           ├── logo.png
│           └── categorieDangers/    # Pictogrammes officiels des catégories de risques DigiRisk
├── Data/
│   └── settings/
│       ├── settings.json            # URL Dolibarr, DOLAPIKEY, cache GP/UT et préférences (gitignored)
│       └── queue.json               # File d'attente hors-ligne des risques/photos/tâches
└── Other/
    ├── Help/
    │   └── Images/                  # Assets visuels de help.html
    └── Source/
        ├── Launcher.cs              # Code source C# du lanceur/serveur local
        └── build.ps1                # Script de compilation reproductible (csc.exe / Go)
```

## 3. Règles de Portabilité & Sécurité (Postes Non-Admin)

1. **Zéro élévation UAC (`asInvoker`)** :
   - Les exécutables `DigiRiskPortable.exe` et `App\DigiRisk\DigiRiskApp.exe` doivent toujours embarquer un manifeste `requestedExecutionLevel level="asInvoker"` pour ne jamais déclencher de demande de mot de passe administrateur.
2. **Isolation stricte des données (`Data/`)** :
   - Aucune écriture dans le registre Windows (`HKLM` ou `HKCU`) ni dans `%APPDATA%`.
   - Toutes les données utilisateur (URL du serveur DigiRisk, clé API `DOLAPIKEY`, brouillons, cache des unités de travail) sont stockées dans `Data\settings\`.
   - Si `Data\settings\settings.json` n'existe pas au démarrage, il est initialisé depuis `App\DefaultData\settings\settings.json`.
3. **Architecture Anti-CORS & Double Mode** :
   - `DigiRiskPortable.exe` démarre un micro-serveur HTTP local (`127.0.0.1:<port_dynamique>`) qui sert `App/DigiRisk/index.html` et agit comme **proxy API** (`/api-proxy/...`) vers le serveur Dolibarr/DigiRisk distant. Cela élimine 100 % des blocages CORS et certificats.
   - Il ouvre l'interface dans une fenêtre dédiée (`msedge.exe --app=...` avec profil isolé dans `Data\EdgeProfile` ou fenêtre WinForms).
   - **Mode secours sans `.exe`** : Si une politique AppLocker bloque tous les `.exe` tiers, l'utilisateur peut double-cliquer directement sur `App/DigiRisk/index.html` pour utiliser l'application dans son navigateur habituel.

## 4. Endpoints API Dolibarr / DigiRisk utilisés

L'application communique avec le module **DigiRisk (`digiriskdolibarr`)** et l'API REST native de **Dolibarr** :

- **Authentification** :
  - `GET /api/index.php/login?login={user}&password={pass}` (récupération automatique du `DOLAPIKEY`) ou saisie directe de la clé `DOLAPIKEY` dans l'en-tête HTTP `DOLAPIKEY`.
- **Options de création & Unités de travail (GP / UT)** :
  - `GET /api/index.php/digiriskdolibarr/risks/creation-options` : renvoie la liste des éléments actifs (`elements` : GP/UT), les catégories de dangers (`categories`), les sous-catégories (`sub_categories`) et les cotations.
- **Utilisateurs (Responsable de tâche)** :
  - `GET /api/index.php/users?sortfield=t.rowid&sortorder=ASC&limit=200` : liste des collaborateurs pour le champ *Responsable* de la tâche.
- **Création du Risque + Photo + Cotation** :
  - `POST /api/index.php/digiriskdolibarr/risks/create-with-photo` (ou `POST /api/index.php/digiriskdolibarr/risks`)
  - Payload JSON (`method` vaut `"standard"` pour l'évaluation simple par seuil `0, 48, 51, 100` ou `"advanced"` pour l'évaluation avancée multi-critères) :
    ```json
    {
      "fk_element": 12,
      "category": 0,
      "description": "Description du risque",
      "method": "standard",
      "cotation": 48,
      "comment": "Commentaire d'évaluation",
      "photo_base64": "data:image/jpeg;base64,...",
      "gravite": 1,
      "exposition": 1,
      "occurrence": 1,
      "formation": 1,
      "protection": 1
    }
    ```
- **Création de la Tâche associée ("Le bloc Tâche")** :
  - `POST /api/index.php/tasks` : création de la tâche dans le projet DU (`fk_project`), avec `label`, `date_start`, `date_end`, `budget_amount`, et `array_options: { "options_fk_risk": <risk_id> }`.

## 5. Compilation

Pour recompiler les exécutables sans dépendances externes (utilise le compilateur natif Windows `.NET Framework 4.x csc.exe` présent sur 100 % des postes Windows 10/11) :
```powershell
powershell -ExecutionPolicy Bypass -File .\Other\Source\build.ps1
```
