using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections;
using System.Security.Cryptography;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ScannerBinaryPatch <path-to-official-NitroxClient.dll>");
    return 2;
}

string path = Path.GetFullPath(args[0]);
string originalSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
Console.WriteLine($"Original NitroxClient.dll SHA256: {originalSha}");

string tempPath = path + ".scannerpatch.tmp";
try
{
    using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
    {
        ModuleDefinition module = assembly.MainModule;
        if (assembly.Name.Name != "NitroxClient" || assembly.Name.Version.ToString() != "1.8.1.0")
        {
            throw new InvalidOperationException($"Not an official-version NitroxClient 1.8.1.0 assembly: {assembly.Name}");
        }

        // Locate the exact 1.8.1.0 compiler-generated coroutine. The original
        // RestoreMapRoom coroutine is NOT replaced: it must complete first.
        TypeDefinition buildType = module.GetTypes().Single(t =>
            t.Name == "BuildEntitySpawner" && t.Namespace == "NitroxClient.GameLogic.Spawning.Bases");
        TypeDefinition stateMachine = buildType.NestedTypes.Single(t =>
            t.Name.StartsWith("<SpawnAsync>d__", StringComparison.Ordinal));
        MethodDefinition moveNext = stateMachine.Methods.Single(m => m.Name == "MoveNext");
        MethodDefinition? unrelated = null; // Deliberately retain a single target method.
        _ = unrelated;
        Instruction restoreCall = moveNext.Body.Instructions.Single(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference m &&
            m.Name == "RestoreMapRoom" && m.DeclaringType.Name == "InteriorPieceEntitySpawner");
        MethodReference restoreMethod = (MethodReference)restoreCall.Operand;

        if (restoreMethod.Parameters.Count != 2 || restoreMethod.ReturnType.FullName != typeof(IEnumerator).FullName)
        {
            throw new InvalidOperationException("RestoreMapRoom signature has changed; refusing to patch.");
        }

        FieldDefinition thisField = stateMachine.Fields.Single(f => f.Name == "<>4__this");
        FieldDefinition entitiesField = buildType.Fields.Single(f => f.Name == "entities");
        TypeReference mapRoomType = restoreMethod.Parameters[1].ParameterType;

        // Reuse the ORIGINAL assembly's existing framework and Nitrox method
        // references. Do not introduce .NET 9 references into a .NET 4.7.2 DLL.
        List<MethodReference> calls = module.GetTypes()
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand)
            .OfType<MethodReference>()
            .ToList();

        MethodReference childEntitiesGetter = calls.First(m =>
            m.Name == "get_ChildEntities" && m.DeclaringType.Name == "Entity");
        MethodReference ofType = calls.OfType<GenericInstanceMethod>().First(m =>
            m.Name == "OfType" && m.DeclaringType.FullName == "System.Linq.Enumerable" &&
            m.GenericArguments.Count == 1 && m.GenericArguments[0].Name == "InventoryItemEntity");
        MethodReference toList = calls.OfType<GenericInstanceMethod>().First(m =>
            m.Name == "ToList" && m.DeclaringType.FullName == "System.Linq.Enumerable" &&
            m.GenericArguments.Count == 1 && m.GenericArguments[0].Name == "Entity");
        MethodReference spawnBatch = calls.First(m =>
            m.Name == "SpawnBatchAsync" && m.DeclaringType.Name == "Entities" &&
            m.Parameters.Count == 3 && m.ReturnType.FullName == typeof(IEnumerator).FullName);

        // Unity runs yielded IEnumerator values recursively. Instead of editing
        // the coroutine state machine or needing Subnautica's proprietary DLLs,
        // replace the single yielded RestoreMapRoom IEnumerator with an
        // IEnumerator over [originalRestore, deferredChipSpawn].
        // Only MoveNext is modified; every other method remains untouched.
        TypeReference iteratorType = new("System.Collections", "IEnumerator", module, module.TypeSystem.CoreLibrary);
        TypeReference enumerableType = new("System.Collections", "IEnumerable", module, module.TypeSystem.CoreLibrary);
        MethodReference getEnumerator = new("GetEnumerator", iteratorType, enumerableType) { HasThis = true };

        moveNext.Body.InitLocals = true;
        VariableDefinition mapRoomLocal = new(mapRoomType);
        VariableDefinition restoreEnumeratorLocal = new(iteratorType);
        moveNext.Body.Variables.Add(mapRoomLocal);
        moveNext.Body.Variables.Add(restoreEnumeratorLocal);

        ILProcessor il = moveNext.Body.GetILProcessor();

        // On entry to RestoreMapRoom(Base, MapRoomEntity), the MapRoomEntity
        // argument is at the top of the evaluation stack. Preserve one copy.
        il.InsertBefore(restoreCall, il.Create(OpCodes.Dup));
        il.InsertBefore(restoreCall, il.Create(OpCodes.Stloc, mapRoomLocal));

        Instruction[] tail =
        [
            il.Create(OpCodes.Stloc, restoreEnumeratorLocal),
            il.Create(OpCodes.Ldc_I4_2),
            il.Create(OpCodes.Newarr, iteratorType),

            // array[0] = original RestoreMapRoom coroutine
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Ldloc, restoreEnumeratorLocal),
            il.Create(OpCodes.Stelem_Ref),

            // array[1] = Entities.SpawnBatchAsync(
            //    mapRoomEntity.ChildEntities.OfType<InventoryItemEntity>()
            //        .ToList<Entity>(), forceRespawn: true, skipFrames: true)
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldfld, thisField),
            il.Create(OpCodes.Ldfld, entitiesField),
            il.Create(OpCodes.Ldloc, mapRoomLocal),
            il.Create(OpCodes.Callvirt, childEntitiesGetter),
            il.Create(OpCodes.Call, ofType),
            il.Create(OpCodes.Call, toList),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Callvirt, spawnBatch),
            il.Create(OpCodes.Stelem_Ref),

            // return IEnumerator that yields the two child coroutines in order
            il.Create(OpCodes.Castclass, enumerableType),
            il.Create(OpCodes.Callvirt, getEnumerator)
        ];
        Instruction last = restoreCall;
        foreach (Instruction instruction in tail)
        {
            il.InsertAfter(last, instruction);
            last = instruction;
        }

        Console.WriteLine($"Patched: {buildType.FullName}/{stateMachine.Name}.MoveNext");
        Console.WriteLine("Added: original RestoreMapRoom coroutine, then forced respawn of Scanner Room InventoryItemEntity children.");

        assembly.Write(tempPath);
    }

    // Round-trip structural validation: exactly one new GetEnumerator
    // invocation in the targeted MoveNext body and a valid patched assembly.
    using (AssemblyDefinition validated = AssemblyDefinition.ReadAssembly(tempPath))
    {
        TypeDefinition owner = validated.MainModule.GetTypes().Single(t =>
            t.Name == "BuildEntitySpawner" && t.Namespace == "NitroxClient.GameLogic.Spawning.Bases");
        MethodDefinition moveNext = owner.NestedTypes.Single(t => t.Name.StartsWith("<SpawnAsync>d__", StringComparison.Ordinal))
            .Methods.Single(m => m.Name == "MoveNext");
        int wrapperCount = moveNext.Body.Instructions.Count(i =>
            i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m &&
            m.Name == "GetEnumerator" && m.DeclaringType.FullName == "System.Collections.IEnumerable");
        if (wrapperCount != 1)
        {
            throw new InvalidOperationException($"Patch verification failed; found {wrapperCount} wrapper calls.");
        }
    }

    File.Copy(tempPath, path, overwrite: true);
    Console.WriteLine($"Patched NitroxClient.dll SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Patch FAILED — did not overwrite original DLL:");
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    if (File.Exists(tempPath))
    {
        File.Delete(tempPath);
    }
}
