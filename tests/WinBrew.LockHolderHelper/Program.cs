using WinBrew.Infrastructure.Locking;

// LockHolderHelper — integration-test child process (A16 real-process lock
// tests). Acquires the mutation lock at <lockPath>, announces its PID on
// stdout, holds for <holdMs>, then exits. Not part of the shipped product.

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: LockHolderHelper <lockPath> <commandName> <holdMs>");
    return 2;
}

var lockPath = args[0];
var commandName = args[1];
var holdMs = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);

var lockMechanism = new ProcessMutationLock(lockPath);

using var handle = await lockMechanism.AcquireAsync(
    commandName,
    TimeSpan.FromSeconds(10),
    CancellationToken.None);

Console.WriteLine($"LOCKED {Environment.ProcessId}");
Console.Out.Flush();

await Task.Delay(holdMs);
return 0;
