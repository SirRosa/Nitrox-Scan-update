using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Collections;
using System.Security.Cryptography;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ScannerBinaryPatch <NitroxClient.dll from official 1.8.1.0>");
    return 2;
}

string path = Path.GetFullPath(args[0]);
string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
Console.WriteLine("Original NitroxClient.dll SHA256: " + originalHash);
string tempPath = path + ".scannerpatch.tmp";
try
{
    using (ModuleDefMD module = ModuleDefMD.Load(path))
    {
        if (module.Assembly?.Name.String != "NitroxClient" ||
            module.Assembly.Version.ToString() != "1.8.1.0")
            throw new InvalidOperationException("Unexpected Nitrox assembly/version — refusing to patch: " + module.Assembly);

        // Find exactly the one IEnumerator state machine from the original 1.8.1.0.
        TypeDef owner = module.GetTypes().Single(t =>
            t.Name.String == "BuildEntitySpawner" &&
            t.Namespace.String == "NitroxClient.GameLogic.Spawning.Bases");
        TypeDef iterator = owner.NestedTypes.Single(t =>
            t.Name.String.StartsWith("<SpawnAsync>d__", StringComparison.Ordinal));
        MethodDef moveNext = iterator.Methods.Single(m => m.Name.String == "MoveNext");
        var instructions = moveNext.Body!.Instructions;

        Instruction restoreCall = instructions.Single(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is IMethod m && m.Name.String == "RestoreMapRoom" &&
            m.DeclaringType?.Name.String == "InteriorPieceEntitySpawner");
        IMethod restore = (IMethod)restoreCall.Operand!;
        if (restore.MethodSig is null || restore.MethodSig.Params.Count != 2 ||
            restore.MethodSig.RetType.FullName != "System.Collections.IEnumerator")
            throw new InvalidOperationException("Unexpected RestoreMapRoom signature");

        FieldDef parent = iterator.Fields.Single(f => f.Name.String == "<>4__this");
        FieldDef entities = owner.Fields.Single(f => f.Name.String == "entities");
        Local room = new(restore.MethodSig.Params[1]);

        // Reuse pre-existing Nitrox and BCL method references; never import
        // .NET 9 signatures into this game-targeting .NET Framework 4.7.2 DLL.
        var calls = module.GetTypes()
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body!.Instructions)
            .Select(i => i.Operand)
            .OfType<IMethod>()
            .ToList();
        IMethod children = calls.First(m =>
            m.Name.String == "get_ChildEntities" &&
            m.DeclaringType?.Name.String == "Entity");
        IMethod inventoryOnly = calls.OfType<MethodSpec>().First(m =>
            m.Name.String == "OfType" && m.DeclaringType?.FullName == "System.Linq.Enumerable" &&
            m.GenericInstMethodSig?.GenericArguments.Count == 1 &&
            m.GenericInstMethodSig.GenericArguments[0].FullName.EndsWith(".InventoryItemEntity", StringComparison.Ordinal));
        IMethod toList = calls.OfType<MethodSpec>().First(m =>
            m.Name.String == "ToList" && m.DeclaringType?.FullName == "System.Linq.Enumerable" &&
            m.GenericInstMethodSig?.GenericArguments.Count == 1 &&
            m.GenericInstMethodSig.GenericArguments[0].FullName.EndsWith(".Entity", StringComparison.Ordinal));
        IMethod spawn = calls.First(m =>
            m.Name.String == "SpawnBatchAsync" && m.DeclaringType?.Name.String == "Entities" &&
            m.MethodSig?.Params.Count == 3);

        // IEnumerator[] returns an IEnumerator; Unity executes yielded nested
        // IEnumerators sequentially. This avoids rewriting the iterator's own
        // state dispatch and preserves the full original RestoreMapRoom behavior.
        TypeRef iterable = new TypeRefUser(module, "System.Collections", "IEnumerable", module.CorLibTypes.AssemblyRef);
        TypeRef enumerator = new TypeRefUser(module, "System.Collections", "IEnumerator", module.CorLibTypes.AssemblyRef);
        IMethod getEnumerator = new MemberRefUser(module, "GetEnumerator",
            MethodSig.CreateInstance(new ClassSig(enumerator)), iterable);

        Local restoreCoroutine = new(new ClassSig(enumerator));
        moveNext.Body.Variables.Add(room);
        moveNext.Body.Variables.Add(restoreCoroutine);
        moveNext.Body.InitLocals = true;

        // On call RestoreMapRoom(Base, MapRoomEntity), the room parameter is
        // top of stack. Capture it without changing either original argument.
        int pos = instructions.IndexOf(restoreCall);
        instructions.Insert(pos++, Instruction.Create(OpCodes.Dup));
        instructions.Insert(pos++, Instruction.Create(OpCodes.Stloc, room));

        Instruction[] tail =
        [
            Instruction.Create(OpCodes.Stloc, restoreCoroutine),
            Instruction.Create(OpCodes.Ldc_I4_2),
            Instruction.Create(OpCodes.Newarr, enumerator),

            // enumerators[0] = original RestoreMapRoom iterator
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Ldloc, restoreCoroutine),
            Instruction.Create(OpCodes.Stelem_Ref),

            // enumerators[1] = entities.SpawnBatchAsync(room.ChildEntities
            //              .OfType<InventoryItemEntity>().ToList<Entity>(),
            //               forceRespawn:true, skipFrames:true)
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, parent),
            Instruction.Create(OpCodes.Ldfld, entities),
            Instruction.Create(OpCodes.Ldloc, room),
            Instruction.Create(OpCodes.Callvirt, children),
            Instruction.Create(OpCodes.Call, inventoryOnly),
            Instruction.Create(OpCodes.Call, toList),
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Callvirt, spawn),
            Instruction.Create(OpCodes.Stelem_Ref),

            // Enumerator over both nested coroutine steps.
            Instruction.Create(OpCodes.Castclass, iterable),
            Instruction.Create(OpCodes.Callvirt, getEnumerator)
        ];

        pos = instructions.IndexOf(restoreCall) + 1;
        foreach (Instruction i in tail)
            instructions.Insert(pos++, i);

        // Inserted IL changes relative branch offsets; normalize all short branches before writing.
        moveNext.Body.SimplifyBranches();
        moveNext.Body.OptimizeBranches();
        Console.WriteLine("Patched exactly one method: " + iterator.FullName + ".MoveNext");
        module.Write(tempPath);
    }

    using (ModuleDefMD check = ModuleDefMD.Load(tempPath))
    {
        var iterator = check.GetTypes().Single(t =>
            t.Name.String.StartsWith("<SpawnAsync>d__", StringComparison.Ordinal) &&
            t.DeclaringType?.Name.String == "BuildEntitySpawner");
        var method = iterator.Methods.Single(m => m.Name.String == "MoveNext");
        int count = method.Body!.Instructions.Count(i =>
            i.OpCode == OpCodes.Callvirt && i.Operand is IMethod m &&
            m.Name.String == "GetEnumerator" && m.DeclaringType?.FullName == "System.Collections.IEnumerable");
        if (count != 1)
            throw new InvalidOperationException($"Patch verification failed; wrapper count={count}");
    }
    File.Copy(tempPath, path, overwrite: true);
    Console.WriteLine("Patched NitroxClient.dll SHA256: " +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("Patch failed; original DLL preserved: " + e);
    return 1;
}
finally
{
    if (File.Exists(tempPath))
        File.Delete(tempPath);
}
