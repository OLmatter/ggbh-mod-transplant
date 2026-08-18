# ReflectionOnly probe for Assembly-CSharp interop types.
# Usage: set $dir to your copied MelonLoader/Il2CppAssemblies folder first.
# WARNING: ReflectionOnly gives UNRELIABLE results on Il2Cpp interop assemblies
#          (phantom types, wrong array/scalar signatures). Verify anything
#          critical with tools/sig_check.cs (compiler-verified) instead.
$ErrorActionPreference = 'SilentlyContinue'
$dir = '%DLL_DIR%'
[System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
  param($s, $e)
  $n = (New-Object System.Reflection.AssemblyName($e.Name)).Name
  $p = Join-Path $dir ($n + '.dll')
  if (Test-Path $p) { return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($p) }
  try { return [System.Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch { return $null }
})
$asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $dir 'Assembly-CSharp.dll'))
$types = @()
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }
$t = $types | Where-Object { $_.FullName -eq 'WorldUnitLogMgr' } | Select-Object -First 1
foreach ($mm in $t.GetMethods()) {
  if ($mm.Name -match 'Merage|WriteLogData') {
    $pars = @()
    foreach ($pa in $mm.GetParameters()) {
      $ga = @(); try { $ga = $pa.ParameterType.GetGenericArguments() | ForEach-Object { $_.Name } } catch {}
      $pars += ($pa.ParameterType.Name + "[" + ($ga -join ',') + "] " + $pa.Name)
    }
    Write-Output ($mm.ReturnType.Name + " " + $mm.Name + "(" + ($pars -join ' ; ') + ")")
  }
}
