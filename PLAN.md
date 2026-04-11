# PLAN — Optimisations de performance et fenêtre d'options

## Instructions

* Effectuer les demandes par petits blocs et demaner à l'utilisateur de tester avant de passer au bloc suivant. Attendre le go de l'utilisateur pour continuer.

* Toujours expliquer ce qui a été fait et ce que l'utilisateur doit tester et confirmer.

* Toujours poser des questions à l'utilisateur si des choses manquent de contexte ou semblent floues.

* En début de refacto, créer une nouvelle branche.

* Une fois qu'un bloc est validé par l'utilisateur, effectuer un commit. Les commits doivent être atomiques.

* Garder en mémoire qu'il faut créer un changelog en fin de refacto.

* Bumper la version en fin de refacto.

## 1. Contexte et objectifs

L'outil analyse les fichiers audio en deux temps. Une phase de collecte énumère le disque et filtre par extension. Une phase d'analyse charge chaque fichier et le passe au décodeur de son format. Les deux phases sont aujourd'hui largement sous-optimales sur une machine moderne.

Un élément central n'a pas été pris en compte jusqu'ici : la nature du support de stockage. Un HDD mécanique et un SSD NVMe se comportent à l'opposé vis-à-vis du parallélisme, du prefetch, et de la localité des accès. Une stratégie unique ne peut pas être optimale sur les deux. Le plan ci-dessous rend la pipeline sensible au matériel et exécute les actions coûteuses une seule fois en amont au lieu de les répéter par fichier.

Contraintes durables : scanner en lecture seule (rule projet), Windows x64 uniquement, .NET 8 Desktop, WinForms. `task format` obligatoire après chaque modification C#.

## 2. État actuel, goulots identifiés

### 2.1 Phase 1, collecte (`Pipeline/FileCollector.cs`)

- Ligne 62, `Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)` énumère tout, puis filtre en mémoire.
- Ligne 46, `new FileInfo(filePath).Length` provoque un second aller-retour kernel alors que `FindFirstFile` a déjà la taille.
- Ligne 39, `Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant()` alloue une chaîne et une copie minuscule, et ce calcul est fait même pour les fichiers rejetés. Ligne 52, `extension.ToUpperInvariant()` refait le travail en sens inverse pour stocker la valeur dans `FileEntry`. Redondance pure.
- Ligne 50, `Path.GetFileName(Path.GetDirectoryName(filePath))` reparse le chemin complet pour chaque fichier alors que le dossier courant est connu pendant la descente.
- Ligne 25, `HashSet<string> seen` déduplique les chemins globalement, utile seulement si deux racines passées en paramètre se chevauchent. Coût mémoire proportionnel à N pour un cas rare.
- Foreach séquentiel lignes 56 à 70 sur les racines, aucune parallélisation même quand les racines pointent sur des volumes physiques distincts.

### 2.2 Phase 2, analyse (`Pipeline/AnalysisPipeline.cs`)

- Ligne 21, `Math.Min(Environment.ProcessorCount, 8)` fige le parallélisme à 8 workers quel que soit le CPU et quel que soit le type de disque.
- Ligne 32, `SemaphoreSlim` créé par appel `RunAsync` (coût faible mais redondant).
- Lignes 43 à 69, `Task.Run` par fichier alloue une closure et passe par le ThreadPool global. Pas de work-stealing dédié, pas de regroupement par format, pas de priorisation par taille.
- Ligne 94, `new Progress<FileProgress>(...)` instancié par fichier. Progress<T> capture le SynchronizationContext et alloue un délégué par instance.
- Ligne 80, `_registry.Resolve(filePath)` relance une résolution par extension pour chaque fichier alors que l'extension a déjà été calculée pendant la collecte.
- Ligne 84, chemin d'erreur recalcule `Path.GetExtension(...).ToUpperInvariant()`.

### 2.3 Checkers FLAC (`Checkers/Flac/NativeFlacChecker.cs`)

- Ligne 180, `File.ReadAllBytes(filePath)` charge tout en RAM sous le GC. Sur 8 workers avec des FLAC 24/96, la pression GC monte vite.
- Lignes 231 à 237, 7 délégués P/Invoke créés par fichier. Les callbacks récupèrent leur état via `GCHandle`, donc aucune capture réelle, donc statisables.
- Ligne 215, `GCHandle.Alloc(state)` par fichier, libéré en fin.
- `FlacMetadataReader.TryReadStreamInfo` est appelé séparément pour lire les 42 premiers octets, alors que le decoder callback `metadata` passe déjà une STREAMINFO parsée par libFLAC. Double parsing évitable.

### 2.4 Checkers MP3 (`Checkers/Mp3/*.cs`)

- `Mp3Checker.cs` ligne 69, `File.ReadAllBytes` identique à FLAC.
- `Mp3Mpg123Backend.cs` ligne 84, `new byte[9216]` alloué par appel.
- `Mp3StructuralParser.cs` ligne 29, `List<(Mp3Diagnostic, long)>` créé par fichier même quand il reste vide.
- `Mp3Checker.cs` calcule la durée via `Mp3MetadataReader` avant la passe structurelle, ce qui consomme le buffer deux fois séquentiellement. Fusion possible.

### 2.5 UI (`UI/*.cs`)

- `MainForm.cs` ligne 21 environ, sans Designer. Menu bar avec File et View, pas de Tools.
- `UserPreferences.cs` persiste en registre HKCU\Software\AudioIntegrityChecker, couvre seulement la géométrie et l'état du panneau d'aide. Rien sur le parallélisme ni la résolution des DLL.
- Résolution DLL actuelle : `NativeLibrary.TryLoad` avec l'ordre dotnet standard, pas de chemin utilisateur.

## 3. Détection du matériel de stockage

### 3.1 Pourquoi c'est nécessaire

Les choix d'optimisation changent totalement selon le type de support :

| Dimension            | HDD mécanique     | SSD SATA    | SSD NVMe              |
| -------------------- | ----------------- | ----------- | --------------------- |
| Seek time            | 5 à 15 ms         | < 0,1 ms    | < 0,05 ms             |
| IOPS random          | 100 à 200         | 10 à 90 k   | 100 à 900 k           |
| Queue depth utile    | 1                 | 4 à 32      | 32 à 256+             |
| Parallélisme lecture | nuisible          | bénéfique   | massivement bénéfique |
| Prefetch             | quasi obligatoire | utile       | marginal              |
| Ordre des accès      | critique          | indifférent | indifférent           |

Lancer 8 workers sur un HDD provoque un thrash de la tête de lecture qui peut diviser le débit par 3 à 5 par rapport à une lecture séquentielle à 1 thread. Appliquer sur NVMe une stratégie "1 reader thread" laisse 90 % du débit du disque sur la table.

### 3.2 Méthode de détection

API Win32 via P/Invoke, sans dépendance `System.Management` :

1. Depuis un chemin de fichier, résoudre la lettre de volume via `GetVolumePathName`.
2. Ouvrir `\\.\X:` avec `FILE_READ_ATTRIBUTES` et `FILE_SHARE_READ | FILE_SHARE_WRITE`.
3. `DeviceIoControl(IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS)` retourne le ou les `PhysicalDiskNumber` qui portent ce volume. Utile quand une partition s'étend sur plusieurs disques, ou pour détecter que C: et D: partagent le même disque physique.
4. Ouvrir `\\.\PhysicalDriveN` en `FILE_READ_ATTRIBUTES`.
5. `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty)` retourne `DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty`. True = HDD, False = SSD ou NVMe.
6. `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY, StorageAdapterProperty)` retourne `STORAGE_ADAPTER_DESCRIPTOR.BusType`. `BusTypeNvme = 17` permet de distinguer un NVMe d'un SSD SATA.

Résultat : enum `StorageKind { Hdd, SataSsd, Nvme, Unknown }`.

### 3.3 Cache et invalidation

Les résultats sont cachés dans un dictionnaire `ConcurrentDictionary<int, StorageKind>` indexé par `PhysicalDiskNumber`, rempli paresseusement au premier appel. Pas d'invalidation runtime : la topologie des disques ne change pas pendant un scan. Le cache est remis à zéro au lancement de l'application.

En cas d'échec de détection (permissions refusées, disque réseau, container), fallback à `StorageKind.Unknown` qui utilise la stratégie SATA SSD par défaut (compromis raisonnable).

### 3.4 Regroupement des fichiers par disque physique

Une fois la collecte terminée, les `FileEntry` sont groupés par `PhysicalDiskNumber`. Le pipeline instancie ensuite un sous-pipeline par groupe, chacun avec ses propres paramètres (workers, prefetch, ordre). Les sous-pipelines tournent en parallèle entre eux puisqu'ils sollicitent des disques physiques distincts.

Cas particulier : un scan d'un seul dossier sur un seul disque produit un seul groupe. Aucune overhead dans ce cas.

### 3.5 Fichier unique et point d'entrée

Nouveau fichier `Pipeline/StorageDetector.cs` avec une classe statique `StorageDetector` exposant :

```csharp
static StorageKind Detect(string filePath);
static int GetPhysicalDiskNumber(string filePath);
static StorageKind GetKind(int physicalDiskNumber);
```

Appelé par le pipeline au démarrage du scan, jamais par fichier.

## 4. Matrice de stratégie par type de disque

Pour chaque paramètre clé, valeur par défaut selon le matériel détecté. Ces valeurs sont utilisées par le mode "Automatic" de la fenêtre d'options. L'utilisateur peut forcer d'autres valeurs via le mode manuel.

| Paramètre                                | HDD                                    | SATA SSD                  | NVMe             | Unknown                   |
| ---------------------------------------- | -------------------------------------- | ------------------------- | ---------------- | ------------------------- |
| Workers de décodage                      | `ProcessorCount`                       | `min(ProcessorCount, 8)`  | `ProcessorCount` | `min(ProcessorCount, 8)`  |
| Threads de lecture I/O                   | 1 (dédié)                              | workers                   | workers          | workers                   |
| Modèle I/O                               | producteur/consommateur                | mmap direct               | mmap direct      | mmap direct               |
| Prefetch avance                          | 2 fichiers                             | 1 fichier                 | aucun            | 1 fichier                 |
| Ordre de traitement                      | tri par chemin (proche de l'ordre MFT) | indifférent               | indifférent      | tri par chemin            |
| Taille buffer lecture                    | 1 Mo                                   | 256 Ko                    | 64 Ko            | 256 Ko                    |
| Parallélisation énumération multi-racine | non (même disque)                      | oui si racines distinctes | oui              | oui si racines distinctes |
| Filtre d'extension côté FS               | oui                                    | oui                       | oui              | oui                       |

Justifications clés :

- Sur HDD, les workers CPU sont toujours au maximum parce que le décodage (libFLAC, mpg123) est CPU-bound, mais l'I/O est sérialisé par un seul thread reader. Les workers attendent leur buffer en mémoire, jamais le disque.
- Sur NVMe, il n'y a aucun intérêt à centraliser l'I/O. Chaque worker gère son propre fichier via mmap. Le cache manager Windows paginise à la demande, et le NVMe absorbe les accès concurrents sans pénalité.
- Sur SATA SSD, un cap à 8 workers évite de saturer la queue SATA (32 commandes max).
- Le tri par chemin sur HDD profite du fait que `FindFirstFile` retourne les entrées dans l'ordre de la MFT, qui suit grossièrement l'ordre d'allocation physique. Les fichiers voisins sur disque sont traités dans la foulée, réduisant les seeks.

## 5. Travail d'initialisation unique en amont

Objectif : consolider dans une seule phase de setup tout ce qui peut être calculé une seule fois, et le rendre accessible par référence aux composants downstream.

### 5.1 Inventaire des actions aujourd'hui répétées par fichier

| Action actuelle                                             | Fréquence                | Coût par appel               | Solution                                                                                   |
| ----------------------------------------------------------- | ------------------------ | ---------------------------- | ------------------------------------------------------------------------------------------ |
| `Path.GetExtension + ToLowerInvariant` dans `FileCollector` | par fichier énuméré      | 2 allocations                | filtrage d'extension FS-native, plus de parsing côté C#                                    |
| `ToUpperInvariant` pour `FileEntry.Format`                  | par fichier retenu       | 1 allocation                 | stocker une référence sur une string interned unique par extension                         |
| `new FileInfo(filePath).Length`                             | par fichier retenu       | 1 kernel round-trip          | `DirectoryInfo.EnumerateFiles` retourne `FileInfo` prérempli                               |
| `Path.GetFileName(Path.GetDirectoryName(filePath))`         | par fichier retenu       | 2 parse + alloc              | descente manuelle qui garde le dossier courant en variable                                 |
| `_registry.Resolve(filePath)`                               | par fichier analysé      | lookup dict par extension    | résolution en batch au moment de la construction du plan, résultat stocké dans `FileEntry` |
| 7 délégués P/Invoke FLAC                                    | par fichier FLAC         | 7 allocations                | `static readonly` au niveau classe                                                         |
| `new byte[9216]` buffer mpg123                              | par fichier MP3          | 1 allocation LOH potentielle | `ArrayPool<byte>.Shared`                                                                   |
| `new Progress<FileProgress>(...)`                           | par fichier analysé      | 1 allocation + délégué       | instance unique par pipeline, dispatching interne par fichier courant                      |
| Résolution `libFLAC.dll` / `mpg123.dll`                     | déjà cachée statiquement | rien                         | rien à faire                                                                               |
| Détection HDD/SSD                                           | n'existe pas encore      | DeviceIoControl quelques ms  | cache `ConcurrentDictionary` par `PhysicalDiskNumber`, rempli une fois au début du scan    |
| `FrozenSet<string>` extensions supportées                   | une fois par scan        | allocation unique            | passer en `static readonly` au niveau de `CheckerRegistry`                                 |
| Parsing frame tables MP3 (bitrate, freq)                    | déjà des `const`         | rien                         | rien                                                                                       |

### 5.2 Objet `ScanContext`

Nouveau type `Pipeline/ScanContext.cs` construit une seule fois en début de scan et passé par référence à tout le pipeline downstream :

```csharp
internal sealed class ScanContext
{
    public FrozenSet<string> SupportedExtensions { get; }
    public IReadOnlyDictionary<string, IFormatChecker> CheckersByExtension { get; }
    public IReadOnlyDictionary<int, StorageKind> StorageByDisk { get; }
    public int DefaultWorkerCountHdd { get; }
    public int DefaultWorkerCountSsd { get; }
    public int DefaultWorkerCountNvme { get; }
    public IProgress<FileProgress> SharedProgressSink { get; }
    public CancellationToken CancellationToken { get; }
}
```

Les checkers ne cherchent plus leur entrée dans un registre à chaque fichier. La référence est déjà dans `FileEntry` (nouveau champ `IFormatChecker Checker`) ou accessible via le dictionnaire `CheckersByExtension` en O(1) sans allocation.

### 5.3 Enrichissement de `FileEntry`

Version étendue :

```csharp
internal sealed record FileEntry(
    string FilePath,
    string DirectoryName,
    string Format,           // valeur string unique interned (pas de ToUpper par fichier)
    long Bytes,
    int PhysicalDiskNumber,  // rempli pendant la collecte
    IFormatChecker Checker   // résolu pendant la collecte
);
```

Le pipeline d'analyse n'a plus aucune résolution à faire par fichier. Il prend `entry.Checker.Check(...)` directement.

### 5.4 Pré-création des structures natives réutilisables

Pool par worker, créé à l'ouverture du sous-pipeline, libéré à la fin :

- Un décodeur FLAC `FLAC__StreamDecoder*` par worker, réinitialisé entre fichiers via `FLAC__stream_decoder_flush`.
- Un handle mpg123 par worker, recyclé via `mpg123_close` + `mpg123_feed`.
- Un buffer de sortie PCM loué depuis `ArrayPool<byte>.Shared` au début de chaque fichier et rendu à la fin.

Voir P_B.5 pour le détail et les risques autour de la réutilisation des décodeurs.

## 6. Partie A — Collecte adaptative

### P_A.1 — Filtrage d'extension au niveau du système de fichiers

**Constat.** `FileCollector.cs:62` énumère tout et filtre en mémoire. Sur une bibliothèque musicale réelle, 40 à 60 % des entrées retournées par le FS sont des pochettes, cue, log, txt, inutilement stat-ées.

**Solution.** Boucler sur les extensions supportées et appeler `dir.EnumerateFiles("*." + ext, EnumerationOptions)` pour chacune. Chaque appel demande au driver FS un motif unique, qui est filtré côté kernel via `FindFirstFileEx`.

```csharp
var options = new EnumerationOptions {
    RecurseSubdirectories = true,
    IgnoreInaccessible = true,
    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
    BufferSize = 65536,
    MatchType = MatchType.Win32,
};
```

`BufferSize = 65536` élargit le buffer `FindFirstFile` de 4 Ko à 64 Ko, ce qui compte sur les dossiers à plusieurs milliers de fichiers.

**Impact matériel.** Gain massif sur HDD (chaque entrée rejetée économise potentiellement un stat), gain modéré sur SSD.

**Test.** Corpus mixte de 50 000 fichiers dont 10 % audio. Mesurer le temps de Phase 1 sur HDD et sur NVMe. Cible HDD : division par 2 du temps. Cible NVMe : gain de 20 à 30 %.

**Risque.** Faible.

### P_A.2 — Lecture de la taille sans second stat

**Constat.** `FileCollector.cs:46`, `new FileInfo(filePath).Length`. Un vrai round-trip kernel par fichier retenu.

**Solution.** Passer à `DirectoryInfo.EnumerateFiles(...)` qui retourne des `FileInfo` préremplis. La taille est lue depuis la structure `WIN32_FIND_DATA` renvoyée par `FindFirstFile`, aucun appel supplémentaire.

**Impact matériel.** Gain spectaculaire sur HDD (un seek évité par fichier). Gain mesurable sur SATA SSD. Marginal sur NVMe.

**Test.** Corpus de 5000 FLAC sur HDD. Cible : réduction de 60 % du temps de Phase 1.

**Risque.** Très faible.

### P_A.3 — Suppression du double casting d'extension

**Constat.** `FileCollector.cs:39` fait `ToLowerInvariant` et `FileCollector.cs:52` fait `ToUpperInvariant` sur la même extension du même fichier.

**Solution.** Comme P_A.1 fait le filtrage au niveau FS, on connaît déjà l'extension de chaque batch. On la pré-calcule une seule fois par extension supportée (interning au niveau `ScanContext.SupportedExtensions`), et chaque `FileEntry` stocke une référence à la string interned. Zéro allocation par fichier.

**Impact matériel.** Pas dépendant, uniquement allocations et pression GC.

**Test.** Profilage allocations sur 10 000 fichiers. Cible : zéro allocation de string dans la boucle de collecte.

**Risque.** Très faible.

### P_A.4 — Descente manuelle pour capturer le nom de dossier

**Constat.** `FileCollector.cs:50` reparse le chemin complet pour retrouver le dossier courant, alors que la descente récursive connaît forcément le dossier où elle est.

**Solution.** Remplacer `EnumerateFiles(..., AllDirectories)` par une descente manuelle :

```csharp
void Walk(DirectoryInfo dir, string directoryName) {
    foreach (var ext in supportedExtensions)
        foreach (var fi in dir.EnumerateFiles("*." + ext, options))
            Add(fi, directoryName);
    foreach (var sub in dir.EnumerateDirectories())
        Walk(sub, sub.Name);
}
```

Le `directoryName` est calculé une fois par dossier au lieu d'une fois par fichier.

**Impact matériel.** Pas dépendant. Nuance : cette forme permet d'appliquer P_A.5 (parallélisme par sous-dossier) de façon naturelle.

**Test.** Profilage allocations. Cible : zéro allocation liée au parsing de chemin dans la boucle de collecte.

**Risque.** Moyen. La descente manuelle doit gérer les erreurs d'accès par dossier (try local sur le `EnumerateDirectories` ou `EnumerateFiles`).

### P_A.5 — Parallélisation multi-racine et par sous-dossier adaptative

**Constat.** `FileCollector.cs:56` traite les racines en séquence.

**Solution.** Deux niveaux :

1. Grouper les racines par `PhysicalDiskNumber` (via `StorageDetector`).
2. Paralléliser entre groupes (disques physiques différents = zéro contention).
3. À l'intérieur d'un groupe, la stratégie dépend du type :
   - HDD : boucle séquentielle sur les racines et sous-dossiers. Aucun parallélisme de lecture.
   - SSD/NVMe : `Parallel.ForEach` sur les sous-dossiers de premier niveau, avec `MaxDegreeOfParallelism` modéré (4 suffit pour saturer l'énumération FS sur NVMe).

**Impact matériel.** Critique. Sur HDD, ignorer cette distinction fait régresser les perfs. Sur NVMe, l'ignorer laisse 50 % du potentiel sur la table.

**Test.** Corpus à deux racines sur le même HDD : doit être séquentiel, sans surcoût. Corpus à deux racines sur deux disques différents : doit paralléliser. Corpus NVMe : parallélisme interne visible.

**Risque.** Moyen. Agrégation dans un `ConcurrentBag<FileEntry>` puis tri final.

### P_A.6 — Streaming vers le pipeline d'analyse

**Constat.** `FileCollector.Collect` matérialise une liste complète avant que le pipeline ne voie le premier fichier. Sur 100 000 fichiers, 2 à 4 secondes d'attente visuelle sans feedback.

**Solution.** Convertir `FileCollector` en producteur `Channel<FileEntry>`. Le pipeline consomme au fil de l'eau. Le compteur "fichiers trouvés" de l'UI s'incrémente en temps réel pendant que les premiers résultats d'analyse commencent déjà à apparaître.

**Impact matériel.** Latence perçue par l'utilisateur, pas de gain de throughput. Plus utile sur collections très larges.

**Test.** Délai entre clic "Start scan" et premier résultat affiché, sur 100 000 fichiers. Cible : moins de 500 ms.

**Risque.** Moyen. L'UI doit gérer un total inconnu pendant la collecte (barre de progression en mode indéterminé tant que `FileCollector` n'a pas signalé la fin).

## 7. Partie B — Analyse adaptative

### P_B.1 — Workers dimensionnés par type de disque

**Constat.** `AnalysisPipeline.cs:21`, cap fixe à 8.

**Solution.** Le nombre de workers est lu depuis `ScanContext` au démarrage de `RunAsync`. En mode Automatic, la valeur est `ScanContext.DefaultWorkerCountFor(disk.Kind)` :

- HDD : `ProcessorCount` pour le décodage, plus 1 thread reader dédié (P_B.3).
- SATA SSD : `min(ProcessorCount, 8)`.
- NVMe : `ProcessorCount`, sans cap.
- Unknown : `min(ProcessorCount, 8)`.

En mode manuel (fenêtre d'options), la valeur saisie par l'utilisateur override tout.

**Impact matériel.** Essentiel. Le choix dépend entièrement du disque.

**Test.** Benchmark sur 3 machines : HDD mécanique, SATA SSD, NVMe. Vérifier la courbe de throughput en fonction du nombre de workers et confirmer que l'optimum suit les valeurs de la matrice.

**Risque.** Faible côté code, moyen côté mesure (nécessite du matériel varié).

### P_B.2 — Remplacement du `SemaphoreSlim` + `Task.Run`

**Constat.** `AnalysisPipeline.cs:32 à 69` alloue une Task par fichier et paye une closure. Le modèle est compatible mais verbeux.

**Solution.** `Parallel.ForEachAsync(channel.ReadAllAsync(), parallelOptions, async (entry, ct) => { ... })`. Avantages :

- Pas d'allocation de Task par item.
- Work-stealing natif dotnet.
- Backpressure automatique sur la source Channel.
- Support CancellationToken centralisé.

La pause (`PauseController`) s'intègre en appelant `WaitIfPausedAsync` en début de chaque itération.

**Impact matériel.** Uniforme, pas dépendant. Gain de 10 à 20 % sur le CPU hors décodage.

**Test.** Profilage allocations Gen0. Cible : division par 5 des allocations attribuées au dispatcher.

**Risque.** Moyen. La logique de pause doit être revérifiée. Les événements `FileStarted` / `FileCompleted` doivent rester sur le même contrat.

### P_B.3 — Reader thread dédié sur HDD

**Constat.** Sur HDD, lancer 8 workers qui font chacun `File.ReadAllBytes` provoque un thrash. Le disque parcourt la tête en zigzag entre 8 positions différentes.

**Solution.** Architecture producteur/consommateur spéciale HDD :

- 1 thread reader qui parcourt la liste triée de fichiers en séquentiel.
- Pour chaque fichier, il lit le contenu complet dans un `byte[]` loué depuis `ArrayPool` et l'enfile dans un `Channel<LoadedFile>` de capacité bornée (par exemple 4, soit 2 à 3 fichiers de préchargé).
- N workers de décodage consomment le Channel. Quand un décodeur termine, il rend son buffer au pool.

Le disque ne voit donc qu'un seul accès à la fois, en ordre séquentiel. Le décodage CPU se recouvre avec la lecture I/O.

Sur SATA SSD et NVMe, ce modèle est remplacé par un mmap direct : chaque worker mappe lui-même son fichier. Pas de thread reader.

**Impact matériel.** Spécifique HDD. Gain attendu de 50 à 200 % sur HDD. Sur SSD/NVMe, ce chemin n'est pas pris.

**Test.** Scan d'un corpus de 1000 FLAC sur HDD avec et sans reader dédié. Cible : réduction de moitié ou mieux du temps total.

**Risque.** Élevé. Refacto importante du pipeline. À mettre derrière une interface commune `IIoStrategy` qui permet au pipeline de commuter entre `SequentialReaderStrategy` (HDD) et `DirectMmapStrategy` (SSD/NVMe).

### P_B.4 — Memory-mapped files sur SSD/NVMe

**Constat.** `File.ReadAllBytes` dans les checkers FLAC et MP3 provoque une copie kernel→user et alloue un `byte[]` sur le GC.

**Solution.** Sur SSD et NVMe, remplacer par `MemoryMappedFile.CreateFromFile + CreateViewAccessor` puis exposer le contenu via `ReadOnlySpan<byte>` ou pointeur natif.

- `NativeFlacChecker` utilise déjà des callbacks `read/seek/tell/length/eof` : le callback read lit depuis le mapping.
- `Mp3Checker` passe le pointeur du mapping directement à `mpg123_feed` et à `Mp3StructuralParser.Scan`.

Zéro copie, zéro pression GC, et le cache manager Windows paginise à la demande. Avec `PrefetchVirtualMemory` optionnel pour forcer la présence en RAM.

**Impact matériel.** Sur SSD/NVMe, gain de 10 à 20 % et surtout suppression de la pression GC. Sur HDD, mmap est remplacé par P_B.3 (lecture séquentielle dans buffer géré).

**Test.** Comparer RAM peak process pendant un scan de 1000 FLAC 24/96. Cible : RAM plafonnée à 100 Mo au lieu de monter à 2 Go.

**Risque.** Moyen-élevé. Interface `IFileBuffer` commune à introduire. Les checkers doivent accepter un buffer fourni de l'extérieur au lieu de lire eux-mêmes.

### P_B.5 — Pool de décodeurs FLAC et mpg123

**Constat.** Chaque fichier FLAC réalloue un `FLAC__StreamDecoder` et ses tables internes. Chaque fichier MP3 réalloue un handle mpg123.

**Solution.** Pool par worker (ThreadStatic ou par index worker) :

- Au démarrage du sous-pipeline, créer N décodeurs (un par worker).
- Chaque worker réutilise son décodeur. Entre deux fichiers, `FLAC__stream_decoder_flush` ou équivalent mpg123 pour reset l'état.
- À la fin du sous-pipeline, `FLAC__stream_decoder_delete` pour tout le monde.

**Impact matériel.** Pas dépendant directement du type de disque, mais la combinaison avec P_B.2 (work-stealing) rend chaque décodeur stable sur un worker.

**Test.** Corpus de 10 000 petits fichiers (< 5 Mo). Cible : gain de 5 à 15 %.

**Risque.** Moyen. libFLAC ne documente pas toujours proprement le comportement après erreur. Tester agressivement avec des fichiers corrompus pour s'assurer que le reset n'introduit pas de faux positifs sur le fichier suivant. Si un reset foireux est détecté, fallback sur `delete + new` pour ce fichier seulement.

### P_B.6 — Délégués P/Invoke FLAC statiques

**Constat.** `NativeFlacChecker.cs:231-237` alloue 7 délégués par fichier. Les callbacks sont sans capture, ils lisent leur état via `GCHandle.client_data`.

**Solution.** Déclarer les 7 délégués en `private static readonly`. Les passer une seule fois. 7 allocations supprimées par fichier.

**Impact matériel.** Pas dépendant.

**Test.** Profilage allocations. Cible : disparition des entrées callback dans le rapport.

**Risque.** Très faible.

### P_B.7 — `ArrayPool` pour buffers chauds

**Constat.** `Mp3Mpg123Backend.cs:84` alloue `new byte[9216]` par fichier. `Mp3StructuralParser.cs:29` alloue une `List<>` par fichier.

**Solution.** `ArrayPool<byte>.Shared.Rent(9216)` + `Return` dans try/finally. Pour la liste de diagnostics, utiliser une `List<T>` passée par référence depuis le pool du worker, ou mieux, écrire directement dans un `Span<Diagnostic>` de taille bornée puis copier seulement les entrées pertinentes à la fin.

**Impact matériel.** Pas dépendant.

**Test.** Profilage allocations Gen0. Cible : zéro allocation > 1 Ko par fichier MP3.

**Risque.** Faible.

### P_B.8 — `IProgress<T>` unique par pipeline

**Constat.** `AnalysisPipeline.cs:94`, `new Progress<FileProgress>(...)` par fichier. Alloue un Progress et capture un SynchronizationContext par appel.

**Solution.** Une seule instance `IProgress<FileProgress>` au niveau pipeline, qui reçoit `(FilePath, FileProgress)` et dispatche. Suppression de N allocations par scan.

Alternative plus propre : passer un `Action<FileProgress>` au checker, tel quel, sans wrapper Progress. Plus direct, zéro alloc.

**Impact matériel.** Pas dépendant.

**Test.** Profilage allocations. Cible : disparition des entrées `Progress<FileProgress>`.

**Risque.** Faible. Impact sur la signature de `IFormatChecker.Check`, donc touche plusieurs fichiers en même temps.

### P_B.9 — Fusion metadata + analyse

**Constat.** `Mp3Checker.cs` appelle `Mp3MetadataReader` avant la passe structurelle, consommant le buffer deux fois séquentiellement. Pour FLAC, `FlacMetadataReader.TryReadStreamInfo` est appelé séparément du callback metadata de libFLAC.

**Solution.**

- MP3 : intégrer la lecture de durée dans le premier pass du parser structurel. La boucle de scan rencontre de toute façon le premier header, le Xing/Info, et la dernière frame. Publier la durée comme effet de bord de ce pass.
- FLAC : supprimer le pré-parse de STREAMINFO et récupérer la durée dans le callback `metadata` déjà exposé par libFLAC.

**Impact matériel.** Marginal en temps, mais supprime un read redondant. Gain plus visible sur HDD.

**Test.** Comparaison temps par fichier sur un corpus homogène. Cible : gain de 3 à 8 %.

**Risque.** Moyen. La durée doit rester disponible même quand le fichier est corrompu avant que le parser n'atteigne le point où elle est calculée. Fallback sur estimation CBR si le Xing n'a pas été atteint.

### P_B.10 — Tri par chemin sur HDD uniquement

**Constat.** L'ordre d'analyse sur HDD affecte lourdement les seeks.

**Solution.** Dans `ScanContext`, marquer chaque groupe HDD avec un tri stable par chemin avant dispatch vers le pipeline. Ne pas trier sur SSD/NVMe (gain nul, allocation évitée).

Piste expérimentale plus avancée : `FSCTL_GET_RETRIEVAL_POINTERS` retourne les extents physiques d'un fichier. Tri par LCN (Logical Cluster Number) du premier extent. Gain marginal vs tri par chemin dans la plupart des cas, mais peut aider sur des disques très fragmentés. Voir P_E.3.

**Impact matériel.** Spécifique HDD.

**Test.** Corpus de 2000 FLAC sur HDD. Comparaison scan dans l'ordre d'énumération vs scan trié. Cible : gain de 10 à 25 %.

**Risque.** Faible.

## 8. Partie C — Optimisations transversales

### P_C.1 — Résolution checker une seule fois par fichier

**Constat.** `AnalysisPipeline.cs:80` appelle `_registry.Resolve(filePath)` à l'analyse, alors que `FileCollector` connaît déjà l'extension.

**Solution.** Stocker la référence `IFormatChecker` dans `FileEntry` pendant la collecte (voir 5.3). Le pipeline n'appelle jamais plus le registry pendant l'analyse.

**Impact matériel.** Pas dépendant. Gain marginal en temps mais plus propre architecturalement.

**Risque.** Faible.

### P_C.2 — Limitation du taux de progression

**Constat.** Les reports de progress par fichier individuel + reports globaux peuvent saturer la boucle de messages UI sur une grosse collection.

**Solution.** Rate-limiter côté pipeline à 30 reports/sec via un timer. Utiliser un `Channel<ProgressSnapshot>` avec drop des intermédiaires (conserver uniquement le dernier).

**Impact matériel.** Pas dépendant.

**Test.** Scan de 10 000 fichiers de 100 ko. CPU thread UI < 2 %.

**Risque.** Faible.

### P_C.3 — ReadyToRun + PGO dynamique

**Constat.** Le projet publie en single file mais n'active pas `ReadyToRun`. Le JIT tier 1 prend quelques milliers d'appels avant de kicker sur le chemin chaud du parser MP3.

**Solution.** Dans `AudioIntegrityChecker.csproj` :

```xml
<PublishReadyToRun>true</PublishReadyToRun>
<TieredPGO>true</TieredPGO>
```

Si le gain mesuré justifie la complexité : générer un profil PGO via une exécution instrumentée sur un corpus de référence, puis builder avec `PublishReadyToRunComposite=true`.

**Impact matériel.** Pas dépendant.

**Test.** Temps d'analyse des 100 premiers fichiers, mode cold. Cible : réduction de 20 à 40 %.

**Risque.** Faible. Pur build flag.

### P_C.4 — `FileOptions.SequentialScan` sur les lectures

**Constat.** Quand le pipeline lit un fichier via `FileStream` (mode HDD P_B.3), le cache manager Windows ne sait pas que l'accès sera séquentiel.

**Solution.** Ouvrir avec `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan)`. Donne un hint au cache manager : readahead plus agressif, éviction plus rapide des pages déjà consommées.

**Impact matériel.** Utile sur HDD (prefetch kernel automatique), marginal sur SSD, inutile sur NVMe.

**Test.** Bench HDD avant/après.

**Risque.** Très faible.

### P_C.5 — Audit `ConfigureAwait(false)` sur le hot path

**Constat.** Le code actuel l'applique déjà aux endroits critiques. À vérifier que toutes les continuations dans `Pipeline/` et `Checkers/` respectent la règle.

**Solution.** Audit ponctuel, correction si nécessaire.

**Impact matériel.** Pas dépendant.

**Risque.** Très faible.

## 9. Partie D — Pistes expérimentales

### P_E.1 — Exclusion Windows Defender temporaire

**Constat.** Sur une collection neuve, Defender scanne chaque open(). L'overhead peut doubler le temps I/O.

**Solution.** Documenter dans l'UI le fait qu'ajouter temporairement le dossier aux exclusions Defender accélère le premier scan. Ne pas automatiser (nécessite élévation admin).

**Impact matériel.** Visible uniquement en cold cache, indépendant du type de disque.

**Risque.** Aucun si manuel et documenté.

### P_E.2 — Scan du sync word MP3 en SIMD

**Constat.** Le parser structurel MP3 cherche `0xFF` sur 8 bits dans une boucle scalaire. Sur un fichier corrompu sans sync valide, le parser peut scanner des dizaines de Mo.

**Solution.** `Vector256<byte>` (AVX2) pour localiser `0xFF` par blocs de 32 octets, puis valider les 3 bits de sync sur les candidats. Fallback scalaire pour CPU sans AVX2 (rare en 2026).

**Impact matériel.** Pas dépendant du disque. Gain spectaculaire sur fichiers fragmentés, négligeable sur fichiers sains.

**Test.** Fichier MP3 corrompu volontaire (500 Mo de bruit avec headers insérés tous les 10 Mo). Mesurer Pass 1.

**Risque.** Moyen. SIMD subtil, nécessite une version scalaire de référence pour comparer.

### P_E.3 — Tri par LCN sur HDD très fragmenté

**Constat.** Sur un HDD vieux et fragmenté, l'ordre MFT n'est plus corrélé à l'ordre physique.

**Solution.** `FSCTL_GET_RETRIEVAL_POINTERS` retourne les extents physiques d'un fichier. Trier par LCN du premier extent. Ne le faire que si `StorageKind == Hdd` et si la fragmentation dépasse un seuil (détectable via `FSCTL_QUERY_FILE_LAYOUT` ou plus simplement via un échantillonnage).

**Impact matériel.** Spécifique HDD très fragmenté.

**Test.** Bench comparatif sur un HDD avec fragmentation connue. Gain attendu 5 à 20 %.

**Risque.** Élevé. Complexité forte pour un cas d'usage spécifique.

### P_E.4 — Registered I/O ou overlapped I/O direct

**Constat.** `FileStream` et `MemoryMappedFile` utilisent déjà overlapped en interne. Descendre plus bas est improbable.

**Solution.** Non recommandé. Ouvrir cette piste seulement si le profiling post-phase 3 montre une attente I/O résiduelle sur NVMe.

**Risque.** Très élevé pour un gain hypothétique.

### P_E.5 — Décodage GPU

**Constat.** MP3 et FLAC sont inadaptés au GPU. Écarté définitivement.

## 10. Partie E — Fenêtre d'options

### P_F.1 — Architecture

**Forme.** Dialogue modal `OptionsForm : Form`, construit par code comme `MainForm` (pas de Designer, cohérent avec la base de code existante). Lancé depuis un nouveau menu `&Tools > &Options…` ajouté à la menu bar de `MainForm.cs`.

**Layout.**

- `SplitContainer` vertical, panneau gauche fixe (~180 px) avec un `ListBox` ou `TreeView` des catégories.
- Panneau droit : un `Panel` par catégorie, tous préchargés à l'ouverture et rendus visibles à tour de rôle (pas de reconstruction au changement).
- Boutons bas : `OK`, `Cancel`, `Apply`. L'état édité est stocké dans un objet mutable `AppSettingsDraft`, committé vers `UserPreferences` seulement à OK/Apply.

**Taille.** 640 × 460, resize désactivé.

**Nouveau fichier.** `UI/OptionsForm.cs`.

### P_F.2 — Catégorie Performances

**Contenu.**

- Titre : "Performance".

- Sous-section "Threads" :
  
  - `CheckBox` : "Automatic (adapt to storage type)". Coché par défaut.
  - Texte d'info sous la case : "Automatic mode detects whether your files are on an HDD, SSD, or NVMe drive and chooses the optimal thread count per scan." Visible en permanence.
  - `TrackBar` : Minimum = 1, Maximum = `Environment.ProcessorCount`, TickFrequency = 1, visible toujours, mais `Enabled = false` quand la case est cochée. Quand décochée, l'utilisateur peut régler manuellement de 1 à `ProcessorCount`.
  - `Label` à droite du TrackBar : lecture en temps réel, "4 threads" / "16 threads".
  - Label discret sous le TrackBar : "Your CPU has {N} logical cores".

- Sous-section "Storage detection" :
  
  - Petit encart informatif qui liste les disques détectés sur lesquels l'utilisateur a déjà scanné au moins une fois : "Disk 0 (NVMe), Disk 1 (HDD)". Si aucun scan n'a encore été fait, texte "No scan performed yet".

**Comportement.**

- Case cochée : la valeur appliquée est celle de la matrice Section 4 en fonction du disque détecté au moment du scan.
- Case décochée : la valeur saisie override toutes les détections. Un texte d'avertissement apparaît : "Manual setting may be suboptimal on HDD".
- Les modifications prennent effet au prochain scan. Mention explicite sous le bloc : "Changes apply to the next scan."

**Extensions futures (réserver la place sans implémenter).**

- "Skip MP3 pass 2 (structural only)".
- "Memory map files instead of reading into RAM" (exposer P_B.4 à l'utilisateur).
- "I/O strategy override" avec trois radio : Auto, Sequential reader, Direct mmap.

### P_F.3 — Catégorie Tools

**Contenu.** Deux blocs symétriques, un par lib.

Bloc libFLAC :

- Label titre : "libFLAC".
- `TextBox` read-only affichant le chemin actuel résolu ou "Not found".
- `PictureBox` 16×16 à droite du TextBox affichant `accept_button.png` ou `cross.png` selon le statut.
- `Button "Browse…"` : `OpenFileDialog` filtré sur `libFLAC.dll` et `*.dll`.
- `Button "Search in PATH"` : parcourt `Environment.GetEnvironmentVariable("PATH")`, teste la présence du fichier dans chaque dossier, applique le premier hit.
- `Button "Reset to default"` : vide le chemin custom, retour à `NativeLibrary.TryLoad("libFLAC.dll")` standard.

Bloc mpg123 : identique pour `mpg123.dll`.

**Validation live.** Quand l'utilisateur change un chemin via Browse ou Search in PATH, tester immédiatement `NativeLibrary.Load(newPath)` dans un try/catch. Vert + tooltip "Loaded successfully (version X.Y.Z)" si OK, rouge + message d'erreur Win32 sinon. L'icône est recalculée à l'ouverture de la fenêtre et à chaque modification.

**Application au runtime.** À l'OK/Apply :

1. Sauvegarder le chemin dans `UserPreferences`.
2. Décharger la lib courante si possible via `NativeLibrary.Free`.
3. Recharger via `NativeLibrary.Load(customPath)`.
4. Mettre à jour les indicateurs de statut dans `MainForm.cs` (status bar lignes 311-343).

Pré-requis technique : les checkers FLAC et MP3 doivent passer d'un attribut `[DllImport]` statique à une résolution dynamique via `NativeLibrary.Load` + `Marshal.GetDelegateForFunctionPointer`. Refacto locale aux fichiers natifs, à faire avant P_F.3. Si elle est jugée trop lourde, fallback : afficher un message "Restart required" après modification, sans rechargement à chaud.

### P_F.4 — Icônes d'état

**Sources.** Les deux PNG 16×16 fournis :

- `https://ewauq.github.io/fatcow-search/icons/colored/16x16/accept_button.png`
- `https://ewauq.github.io/fatcow-search/icons/colored/16x16/cross.png`

**Intégration.** Téléchargement manuel une seule fois, puis les deux PNG sont placés dans `UI/Icons/` et déclarés comme `<EmbeddedResource>` dans `AudioIntegrityChecker.csproj`. Chargement runtime via `Assembly.GetManifestResourceStream(...)` + `Image.FromStream(...)`. Aucun téléchargement au runtime (offline-first).

### P_F.5 — Persistance

**Extension de `UI/UserPreferences.cs`.** Ajouter dans la classe existante (sous-clé registre `HKCU\Software\AudioIntegrityChecker`) :

- `WorkerCountAuto: bool` (default `true`)
- `WorkerCount: int` (default `Environment.ProcessorCount`)
- `LibFlacCustomPath: string` (default `""`)
- `Mpg123CustomPath: string` (default `""`)

Même mécanisme que les clés actuelles (géométrie, HelpPanelVisible). Pas de nouveau fichier JSON ou ini.

### P_F.6 — Intégration MainForm

- Ajouter un `ToolStripMenuItem "&Tools"` dans la menu bar existante (`MainForm.cs:277-306`).
- Sous-item `&Options…` qui ouvre `OptionsForm`.
- Le compteur "Workers" de la status bar doit être mis à jour dès qu'un scan démarre, en lisant la valeur effective (auto ou manuel).
- Ajouter un nouvel indicateur status bar "Storage: NVMe" à côté des indicateurs libFLAC/mpg123, rempli quand un scan démarre et effacé à la fin. Utile pour valider visuellement que la détection fonctionne.

## 11. Instrumentation et mesure

### P_G.1 — Corpus de référence reproductibles

Constituer trois corpus avec checksums publics :

- **Corpus A** : 1000 FLAC 16/44, taille moyenne 25 Mo, tous sains.
- **Corpus B** : 5000 MP3 CBR 320, taille moyenne 8 Mo, tous sains.
- **Corpus C** : 500 fichiers mixtes, 10 % corrompus volontairement, pour la non-régression fonctionnelle.

Chaque corpus est stocké deux fois : une copie sur HDD et une copie sur NVMe, pour permettre les bench comparatifs sur le même contenu.

### P_G.2 — Flag CLI `--bench`

Ajouter à `Program.cs` un mode headless qui prend un chemin, lance le scan, et émet en stdout un JSON :

```json
{
  "phase1_ms": 123,
  "phase2_ms": 4567,
  "total_ms": 4690,
  "files": 1000,
  "bytes": 26214400000,
  "ram_peak_mb": 187,
  "workers_used": 16,
  "storage_kind": "Nvme",
  "issues_found": 2
}
```

Permet de scripter les bench et comparer les itérations sans ouvrir l'UI.

### P_G.3 — Profilage

- `dotnet-trace collect --format speedscope` pendant un scan de référence, hot path identifié.
- `dotnet-counters monitor` pour RAM, GC gen0/1/2, exceptions/sec.
- PerfView ETW kernel file IO provider pour mesurer IOPS et latence disque.

### P_G.4 — Non-régression

Avant chaque merge d'une optimisation, scan du Corpus C et comparaison JSON diff avec le baseline. Aucune régression fonctionnelle admise (les fichiers corrompus doivent toujours être détectés avec les mêmes diagnostics).

## 12. Ordre d'exécution recommandé

Chaque phase touche au maximum 5 fichiers et se termine par `task format` + `dotnet build` + un run sur Corpus A, B, C. L'approbation explicite est requise avant de passer à la suivante.

### Phase 1, quick wins

1. P_B.6 — délégués statiques FLAC (`NativeFlacChecker.cs`).
2. P_B.7 — ArrayPool buffer mpg123 (`Mp3Mpg123Backend.cs`).
3. P_A.1 + P_A.2 — filtre FS-native et FileInfo via DirectoryInfo (`FileCollector.cs`).
4. P_C.3 — ReadyToRun dans `.csproj`.

### Phase 2, détection matérielle et dédoublonnage

5. Création de `Pipeline/StorageDetector.cs` (nouveau fichier).
6. Création de `Pipeline/ScanContext.cs` (nouveau fichier).
7. Enrichissement de `FileEntry` avec `PhysicalDiskNumber` et `Checker` pré-résolu.
8. P_A.3 + P_A.4 — suppression casts et descente manuelle (`FileCollector.cs`).
9. P_C.1 — pipeline utilise `entry.Checker` au lieu de `_registry.Resolve`.

### Phase 3, fenêtre d'options

10. Création de `UI/OptionsForm.cs` avec catégorie Performances.
11. Ajout du menu Tools dans `MainForm.cs`.
12. Extension `UserPreferences.cs`.
13. Catégorie Tools de `OptionsForm` avec détection live.
14. Indicateur status bar "Storage: …".

### Phase 4, parallélisme adaptatif

15. P_B.1 — workers dimensionnés par type de disque.
16. P_A.5 — parallélisation multi-racine par disque physique.
17. P_B.10 — tri par chemin sur groupes HDD.
18. P_B.2 — remplacement du SemaphoreSlim par `Parallel.ForEachAsync` ou `Channel<T>`.

### Phase 5, architecture I/O

19. Introduction de l'interface `IIoStrategy` et refacto des checkers pour accepter un buffer externe.
20. Implémentation `DirectMmapStrategy` pour SSD/NVMe (P_B.4).
21. Implémentation `SequentialReaderStrategy` pour HDD (P_B.3).
22. P_C.4 — `FileOptions.SequentialScan` dans SequentialReaderStrategy.
23. P_A.6 — streaming `Channel<FileEntry>` entre collecte et analyse.

### Phase 6, optimisations de finition

24. P_B.5 — pool de décodeurs FLAC/mpg123 par worker.
25. P_B.8 — `IProgress<T>` unique par pipeline.
26. P_B.9 — fusion metadata + analyse.
27. P_C.2 — rate limit progress.

### Phase 7, pistes expérimentales (optionnelles)

28. P_E.2 — SIMD scan sync word MP3 (si profiling le justifie).
29. P_E.3 — tri par LCN (si utilisateur signale des HDD fragmentés).
30. P_E.1 — doc exclusion Defender.

Aucune phase ne démarre sans validation des mesures de la précédente. Les gains annoncés dans chaque point sont des cibles à atteindre, pas des garanties : chaque run sur Corpus A/B/C décide si l'optimisation reste ou est revertée.
