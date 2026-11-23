#nullable enable

using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Testing
{
    /// <summary>
    /// Creates the Scene objects a harness fixture needs (SPEC §M.6 item 1) —
    /// <c>HarnessObjects.NewGameObject("T")</c> — on the understanding that the
    /// harness, not the test, throws them away:
    /// <see cref="OperationTestHarness.Dispose"/> destroys every object handed
    /// out this way before it closes the temporary scene, so a fixture never
    /// cleans up after itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An object always lands in the owning harness's own temporary scene, even
    /// when some other scene is active, so a fixture cannot leak an object into
    /// the developer's scene by accident. With more than one live harness the
    /// most recently created one owns the object — the innermost fixture is the
    /// one that asked for it.
    /// </para>
    /// <para>
    /// Only a <see cref="GameObject"/> root is tracked. An object created under
    /// one of them — a component via <c>AddComponent</c>, a child
    /// <see cref="GameObject"/> — goes with its root when the root is destroyed,
    /// which is why the child case needs no API of its own.
    /// </para>
    /// </remarks>
    public static class HarnessObjects
    {
        /// <summary>
        /// Creates <paramref name="name"/> in the harness scene of the running
        /// harness — the most recently created live harness, which under a
        /// <c>using</c> fixture is the harness the caller created.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No <see cref="OperationTestHarness"/> is live, so no temporary scene
        /// exists to own the object.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is null or empty.
        /// </exception>
        public static GameObject NewGameObject(string name) => Create(name, null);

        /// <summary>
        /// Creates <paramref name="name"/> in <paramref name="harness"/>'s own
        /// temporary scene, opening that scene if no run needed it yet, and
        /// makes that harness destroy it on
        /// <see cref="OperationTestHarness.Dispose"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="harness"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is null or empty.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// <paramref name="harness"/> was already disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The harness could not open its temporary scene.
        /// </exception>
        public static GameObject NewGameObject(
            OperationTestHarness harness, string name)
        {
            if (harness is null)
            {
                throw new ArgumentNullException(nameof(harness));
            }
            return Create(name, harness);
        }

        private static GameObject Create(string name, OperationTestHarness? harness)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A harness object must have a name.", nameof(name));
            }
            var owner = harness ?? MostRecent();
            if (owner is null)
            {
                throw new InvalidOperationException(
                    "HarnessObjects.NewGameObject needs a live OperationTestHarness " +
                    "to own the object; create one with OperationTestHarness.Create().");
            }
            var scene = owner.OwnedScene();
            var gameObject = new GameObject(name);
            if (gameObject.scene.handle != scene.handle)
            {
                SceneManager.MoveGameObjectToScene(gameObject, scene);
            }
            owner.Track(gameObject);
            return gameObject;
        }

        private static OperationTestHarness? MostRecent()
        {
            var live = OperationTestHarness.LiveHarnesses();
            return live.Count == 0 ? null : live[live.Count - 1];
        }
    }
}
