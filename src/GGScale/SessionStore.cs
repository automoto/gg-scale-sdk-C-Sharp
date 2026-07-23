using System;
using System.IO;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// Persists a session across process restarts so a game resumes the
    /// same identity instead of registering a fresh anonymous player.
    /// Implementations decide where (file, PlayerPrefs, cloud save …).
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>
        /// Returns the persisted session, or null when none is usable. A
        /// session without a refresh token is not usable (it cannot outlive
        /// its access token) and loads as null.
        /// </summary>
        Session? Load();

        /// <summary>Persists the session, replacing any previous one.</summary>
        void Save(Session session);
    }

    /// <summary>
    /// File-backed <see cref="ISessionStore"/>. Unity consumers should
    /// point the path inside Application.persistentDataPath; see
    /// <see cref="DefaultPath"/> for desktop/server processes.
    /// </summary>
    public sealed class FileSessionStore : ISessionStore
    {
        private readonly string _path;

        /// <summary>Creates a store persisting to <paramref name="path"/>.</summary>
        public FileSessionStore(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path is required", nameof(path));
            }
            _path = path;
        }

        /// <summary>
        /// A per-game session file under the user's application-data
        /// directory (falling back to the temp directory).
        /// </summary>
        public static string DefaultPath(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                throw new ArgumentException("gameId is required", nameof(gameId));
            }
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.GetTempPath();
            }
            return Path.Combine(root, "ggscale", gameId, "session.json");
        }

        /// <inheritdoc />
        public Session? Load()
        {
            string raw;
            try
            {
                raw = File.ReadAllText(_path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            JsonValue parsed;
            try
            {
                parsed = JsonValue.Parse(raw);
            }
            catch (FormatException)
            {
                return null;
            }

            var session = Session.FromJson(parsed);
            // Without a refresh token there is no way to recover from an
            // expired access token; treat as no persisted session.
            if (session.RefreshToken.Length == 0)
            {
                return null;
            }
            return session;
        }

        /// <inheritdoc />
        public void Save(Session session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var doc = JsonValue.NewObject()
                .Set("access_token", JsonValue.Of(session.AccessToken))
                .Set("refresh_token", JsonValue.Of(session.RefreshToken))
                .Set("player_id", JsonValue.Of(session.PlayerId))
                .Set("expires_at", JsonValue.Of(session.ExpiresAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
            File.WriteAllText(_path, doc.ToString());
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
#endif
        }
    }
}
