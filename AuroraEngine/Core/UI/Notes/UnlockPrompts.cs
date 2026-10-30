using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Notes
{
    // Asks for the passwords of locked files a rewrite reached, one prompt at a time; a cancelled one leaves its file as it was.
    public static class UnlockPrompts
    {
        private static readonly LogChannel Log = LogChannel.For("Encryption");

        private readonly record struct Waiting(string path, string reason, Action<PasswordKey> use);

        private static readonly Queue<Waiting> waiting = new Queue<Waiting>();
        private static bool asking;

        public static void Ask(string path, string reason, Action<PasswordKey> use)
        {            if (FileKeys.Take(path) is PasswordKey known)
            {
                Use(new Waiting(path, reason, use), known);
                return;
            }

            waiting.Enqueue(new Waiting(path, reason, use));
            Next();
        }

        private static void Next()
        {
            if (asking || waiting.Count == 0) return;
            asking = true;
            Prompt(waiting.Peek(), null);
        }

        private static void Prompt(Waiting item, string? error)
        {
            if (PasswordWindow.isOpen)
            {
                Task.Delay(100).ContinueWith(_ => Engine.Post(() => Prompt(item, error)));
                return;
            }

            PasswordWindow.Ask(UIActions.Invoking(), $"{item.reason} {Path.GetFileName(item.path)}", false, error,
                password => Task.Run(() => EncryptedFile.Unlock(item.path, password)).ContinueWith(task =>
                {
                    PasswordKey? key = task.IsCompletedSuccessfully ? task.Result : null;
                    Engine.Post(() =>
                    {
                        if (key == null)
                        {
                            Prompt(item, "Wrong password");
                            return;
                        }
                        Done();
                        Use(item, key);
                    });
                }),
                () =>
                {
                    Log.Warn($"'{item.path}' stayed locked and was not updated.");
                    Done();
                });
        }

        private static void Done()
        {
            waiting.Dequeue();
            asking = false;
            Next();
        }

        private static void Use(Waiting item, PasswordKey key)
        {
            try
            {
                item.use(key);
            }
            catch (Exception e)
            {
                Log.Warn($"'{item.path}' could not be updated: {e.Message}");
            }
            finally
            {
                if (!FileKeys.IsRemembered(key)) key.Dispose();
            }
        }
    }
}
