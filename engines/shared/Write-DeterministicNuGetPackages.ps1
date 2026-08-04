[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumPackageBytes = 67108864L
$maximumEntryCount = 10000
$contentTypesPath = '[Content_Types].xml'
$canonicalPropertiesPath = (
    'package/services/metadata/core-properties/core.psmdcp')
$relationshipsPath = '_rels/.rels'
$corePropertiesRelationshipType = (
    'http://schemas.openxmlformats.org/package/2006/relationships/' +
    'metadata/core-properties')

if ($null -eq ('GameAgentNuGetCrc32' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
public static class GameAgentNuGetCrc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(byte[] bytes)
    {
        uint value = 0xffffffffu;
        for (int index = 0; index < bytes.Length; index++)
        {
            value = Table[(value ^ bytes[index]) & 0xff] ^ (value >> 8);
        }

        return value ^ 0xffffffffu;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xedb88320u ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
'@
}

function Assert-SafeEntryName {
    param([Parameter(Mandatory)][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name.Length -gt 4096 -or
        $Name.IndexOf([char]0) -ge 0 -or
        $Name.IndexOf('\') -ge 0 -or
        $Name.StartsWith('/', [StringComparison]::Ordinal) -or
        $Name -match '^[a-zA-Z]:' -or
        $Name.EndsWith('/', [StringComparison]::Ordinal)) {
        throw 'A NuGet package contains an unsafe entry name.'
    }

    $segments = @($Name.Split('/'))
    if ($segments.Count -eq 0 -or
        $segments -contains '' -or
        $segments -contains '.' -or
        $segments -contains '..') {
        throw 'A NuGet package contains an unsafe entry path.'
    }
}

function Read-EntryBytes {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchiveEntry]$Entry
    )

    if ($Entry.Length -gt $script:maximumPackageBytes) {
        throw 'A NuGet package entry exceeds the normalization limit.'
    }

    $stream = $Entry.Open()
    $memory = New-Object IO.MemoryStream
    try {
        $buffer = New-Object byte[] 81920
        $total = 0L
        while ($true) {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -eq 0) {
                break
            }
            $total += $read
            if ($total -gt $script:maximumPackageBytes) {
                throw 'A NuGet package entry exceeds the normalization limit.'
            }
            $memory.Write($buffer, 0, $read)
        }
        if ($total -ne $Entry.Length) {
            throw 'A NuGet package entry has an inconsistent length.'
        }
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $stream.Dispose()
    }
}

function Assert-CorePropertiesContentType {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $input = [IO.MemoryStream]::new($Bytes, $false)
    $reader = [Xml.XmlReader]::Create($input, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    $document.XmlResolver = $null
    try {
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
        $input.Dispose()
    }

    $namespace = (
        'http://schemas.openxmlformats.org/package/2006/content-types')
    if ($null -eq $document.DocumentElement -or
        $document.DocumentElement.LocalName -ne 'Types' -or
        $document.DocumentElement.NamespaceURI -ne $namespace) {
        throw 'A NuGet package has an invalid content-types document.'
    }

    $contentType = (
        'application/vnd.openxmlformats-package.' +
        'core-properties+xml')
    $extensionMappings = 0
    $validMappings = 0
    foreach ($node in @($document.DocumentElement.ChildNodes)) {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element -or
            $node.NamespaceURI -ne $namespace) {
            throw 'A NuGet package has an unsupported content-types document.'
        }
        if ($node.LocalName -eq 'Default' -and
            $node.GetAttribute('Extension').Equals(
                'psmdcp',
                [StringComparison]::OrdinalIgnoreCase)) {
            $extensionMappings++
            if ($node.GetAttribute('ContentType').Equals(
                    $contentType,
                    [StringComparison]::Ordinal)) {
                $validMappings++
            }
        }
    }
    if ($extensionMappings -ne 1 -or $validMappings -ne 1) {
        throw (
            'A NuGet package must map core-properties parts ' +
            'exactly once by extension.')
    }
}

function Normalize-Relationships {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$OriginalPropertiesPath
    )

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $input = [IO.MemoryStream]::new($Bytes, $false)
    $reader = [Xml.XmlReader]::Create($input, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    $document.XmlResolver = $null
    try {
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
        $input.Dispose()
    }

    $namespace = (
        'http://schemas.openxmlformats.org/package/2006/relationships')
    if ($null -eq $document.DocumentElement -or
        $document.DocumentElement.LocalName -ne 'Relationships' -or
        $document.DocumentElement.NamespaceURI -ne $namespace) {
        throw 'A NuGet package has an invalid relationships document.'
    }

    $records = [Collections.Generic.SortedDictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $propertiesFound = $false
    foreach ($node in @($document.DocumentElement.ChildNodes)) {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element -or
            $node.LocalName -ne 'Relationship' -or
            $node.NamespaceURI -ne $namespace) {
            throw 'A NuGet package has an unsupported relationships document.'
        }
        foreach ($attribute in @($node.Attributes)) {
            if ($attribute.NamespaceURI -ne [string]::Empty -or
                $attribute.LocalName -notin @(
                    'Id',
                    'Type',
                    'Target',
                    'TargetMode')) {
                throw (
                    'A NuGet package relationship has unsupported ' +
                    'attributes.')
            }
        }

        $id = $node.GetAttribute('Id')
        $type = $node.GetAttribute('Type')
        $target = $node.GetAttribute('Target')
        $targetMode = $node.GetAttribute('TargetMode')
        if ([string]::IsNullOrWhiteSpace($id) -or
            [string]::IsNullOrWhiteSpace($type) -or
            [string]::IsNullOrWhiteSpace($target)) {
            throw 'A NuGet package relationship is incomplete.'
        }
        if (-not [string]::IsNullOrEmpty($targetMode) -and
            $targetMode -notin @('Internal', 'External')) {
            throw 'A NuGet package relationship has an invalid target mode.'
        }

        $normalizedTarget = $target.TrimStart('/').Replace('\', '/')
        $isPropertiesTarget = $normalizedTarget.Equals(
            $OriginalPropertiesPath,
            [StringComparison]::Ordinal)
        $isPropertiesType = $type.Equals(
            $script:corePropertiesRelationshipType,
            [StringComparison]::Ordinal)
        if ($isPropertiesTarget -ne $isPropertiesType) {
            throw (
                'A NuGet package has an inconsistent core-properties ' +
                'relationship.')
        }
        if ($isPropertiesTarget) {
            if ($targetMode -eq 'External') {
                throw (
                    'A NuGet package has an external core-properties ' +
                    'relationship.')
            }
            $target = '/' + $script:canonicalPropertiesPath
            $propertiesFound = $true
        }
        $record = [pscustomobject]@{
                Target = $target
                TargetMode = $targetMode
                Type = $type
            }
        $key = $type + [char]0 + $target + [char]0 + $targetMode
        if ($records.ContainsKey($key)) {
            throw 'A NuGet package contains duplicate relationships.'
        }
        $records.Add($key, $record)
    }
    if (-not $propertiesFound) {
        throw 'A NuGet package has no core-properties relationship.'
    }

    while ($document.DocumentElement.HasChildNodes) {
        $null = $document.DocumentElement.RemoveChild(
            $document.DocumentElement.FirstChild)
    }
    $ordered = @($records.Values)
    for ($index = 0; $index -lt $ordered.Count; $index++) {
        $element = $document.CreateElement('Relationship', $namespace)
        $element.SetAttribute('Id', 'R' + ($index + 1))
        $element.SetAttribute('Type', [string]$ordered[$index].Type)
        $element.SetAttribute('Target', [string]$ordered[$index].Target)
        if (-not [string]::IsNullOrEmpty(
                [string]$ordered[$index].TargetMode)) {
            $element.SetAttribute(
                'TargetMode',
                [string]$ordered[$index].TargetMode)
        }
        $null = $document.DocumentElement.AppendChild($element)
    }

    $output = New-Object IO.MemoryStream
    $writerSettings = [Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $false
    $writerSettings.NewLineHandling = [Xml.NewLineHandling]::None
    $writerSettings.OmitXmlDeclaration = $false
    $writer = [Xml.XmlWriter]::Create($output, $writerSettings)
    try {
        $document.Save($writer)
        $writer.Flush()
        return ,$output.ToArray()
    }
    finally {
        $writer.Dispose()
        $output.Dispose()
    }
}

function Write-DeterministicZip {
    param(
        [Parameter(Mandatory)]
        [Collections.Generic.Dictionary[string, byte[]]]$Entries,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    if ($Entries.Count -gt [uint16]::MaxValue) {
        throw 'A NuGet package has too many entries for deterministic ZIP32.'
    }

    [string[]]$names = @($Entries.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $temporaryPath = (
        $OutputPath + '.tmp.' + [guid]::NewGuid().ToString('N'))
    $stream = [IO.File]::Open(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new(
        $stream,
        [Text.UTF8Encoding]::new($false),
        $true)
    $writeSucceeded = $false
    try {
        $records = New-Object 'Collections.Generic.List[object]'
        foreach ($name in $names) {
            $nameBytes = [Text.Encoding]::UTF8.GetBytes($name)
            $data = $Entries[$name]
            if ($nameBytes.Length -gt [uint16]::MaxValue -or
                $data.LongLength -gt [uint32]::MaxValue -or
                $stream.Position -gt [uint32]::MaxValue) {
                throw 'A NuGet package exceeds deterministic ZIP32 limits.'
            }

            $crc = [GameAgentNuGetCrc32]::Compute($data)
            $localOffset = [uint32]$stream.Position
            $writer.Write([uint32]0x04034b50)
            $writer.Write([uint16]20)
            $writer.Write([uint16]0x0800)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]33)
            $writer.Write([uint32]$crc)
            $writer.Write([uint32]$data.Length)
            $writer.Write([uint32]$data.Length)
            $writer.Write([uint16]$nameBytes.Length)
            $writer.Write([uint16]0)
            $writer.Write($nameBytes)
            $writer.Write($data)
            $records.Add([pscustomobject]@{
                    Crc = $crc
                    DataLength = [uint32]$data.Length
                    LocalOffset = $localOffset
                    NameBytes = $nameBytes
                })
        }

        if ($stream.Position -gt [uint32]::MaxValue) {
            throw 'A NuGet package exceeds deterministic ZIP32 limits.'
        }
        $directoryOffset = [uint32]$stream.Position
        foreach ($record in $records) {
            $writer.Write([uint32]0x02014b50)
            $writer.Write([uint16]20)
            $writer.Write([uint16]20)
            $writer.Write([uint16]0x0800)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]33)
            $writer.Write([uint32]$record.Crc)
            $writer.Write([uint32]$record.DataLength)
            $writer.Write([uint32]$record.DataLength)
            $writer.Write([uint16]$record.NameBytes.Length)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint32]0)
            $writer.Write([uint32]$record.LocalOffset)
            $writer.Write([byte[]]$record.NameBytes)
        }
        $directoryLength = $stream.Position - $directoryOffset
        if ($directoryLength -gt [uint32]::MaxValue) {
            throw 'A NuGet package exceeds deterministic ZIP32 limits.'
        }

        $writer.Write([uint32]0x06054b50)
        $writer.Write([uint16]0)
        $writer.Write([uint16]0)
        $writer.Write([uint16]$records.Count)
        $writer.Write([uint16]$records.Count)
        $writer.Write([uint32]$directoryLength)
        $writer.Write([uint32]$directoryOffset)
        $writer.Write([uint16]0)
        $writer.Flush()
        $writeSucceeded = $true
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
        if (-not $writeSucceeded -and
            (Test-Path -LiteralPath $temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }

    try {
        if (Test-Path -LiteralPath $OutputPath) {
            throw 'The deterministic NuGet destination already exists.'
        }
        [IO.File]::Move($temporaryPath, $OutputPath)
    }
    catch {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        throw
    }
}

$sourceRoot = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $SourcePath))
$sourceItem = Get-Item -LiteralPath $sourceRoot
if (-not $sourceItem.PSIsContainer -or
    ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The NuGet source must be a regular directory.'
}

$destinationRoot = [IO.Path]::GetFullPath($DestinationPath)
if ($sourceRoot.TrimEnd('\', '/').Equals(
        $destinationRoot.TrimEnd('\', '/'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The deterministic NuGet destination must differ from the source.'
}
if (-not (Test-Path -LiteralPath $destinationRoot)) {
    $null = New-Item -ItemType Directory -Path $destinationRoot
}
$destinationItem = Get-Item -LiteralPath $destinationRoot
if (-not $destinationItem.PSIsContainer -or
    ($destinationItem.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The NuGet destination must be a regular directory.'
}

$packages = @(
    Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.nupkg' |
        Sort-Object Name)
if ($packages.Count -eq 0) {
    throw 'The NuGet source contains no packages.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$createdPackages = New-Object 'Collections.Generic.List[string]'
try {
    foreach ($package in $packages) {
        if ($package.Length -gt $maximumPackageBytes -or
            ($package.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A NuGet source package is unsafe or exceeds the limit.'
        }

        $entries = [Collections.Generic.Dictionary[string, byte[]]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        $totalBytes = 0L
        try {
            if ($archive.Entries.Count -gt $maximumEntryCount) {
                throw 'A NuGet package has too many entries.'
            }
            foreach ($entry in $archive.Entries) {
                Assert-SafeEntryName -Name $entry.FullName
                if ($entries.ContainsKey($entry.FullName)) {
                    throw 'A NuGet package contains duplicate entry names.'
                }
                $bytes = Read-EntryBytes -Entry $entry
                $totalBytes += $bytes.LongLength
                if ($totalBytes -gt $maximumPackageBytes) {
                    throw (
                        'A NuGet package exceeds the expanded ' +
                        'normalization limit.')
                }
                $entries.Add($entry.FullName, $bytes)
            }
        }
        finally {
            $archive.Dispose()
        }

        $propertiesEntries = @(
            $entries.Keys |
                Where-Object {
                    $_ -match (
                        '^package/services/metadata/core-properties/' +
                        '[^/]+[.]psmdcp$')
                })
        if ($propertiesEntries.Count -ne 1 -or
            -not $entries.ContainsKey($relationshipsPath) -or
            -not $entries.ContainsKey($contentTypesPath)) {
            throw 'A NuGet package has an unsupported OPC metadata layout.'
        }

        Assert-CorePropertiesContentType `
            -Bytes $entries[$contentTypesPath]
        $originalPropertiesPath = [string]$propertiesEntries[0]
        $propertiesBytes = $entries[$originalPropertiesPath]
        $relationshipsBytes = Normalize-Relationships `
            -Bytes $entries[$relationshipsPath] `
            -OriginalPropertiesPath $originalPropertiesPath
        $null = $entries.Remove($originalPropertiesPath)
        $entries[$canonicalPropertiesPath] = $propertiesBytes
        $entries[$relationshipsPath] = $relationshipsBytes

        $destinationPackage = Join-Path $destinationRoot $package.Name
        Write-DeterministicZip `
            -Entries $entries `
            -OutputPath $destinationPackage
        $createdPackages.Add($destinationPackage)
    }
}
catch {
    foreach ($createdPackage in $createdPackages) {
        if ([IO.File]::Exists($createdPackage)) {
            [IO.File]::Delete($createdPackage)
        }
    }
    throw
}

Write-Output (
    'DETERMINISTIC_NUGET_WRITE_PASS packages=' + $packages.Count)
