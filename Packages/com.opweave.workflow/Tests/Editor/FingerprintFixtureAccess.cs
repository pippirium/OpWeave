#nullable enable

// T-W2.6-01 test helper: the prefab-fingerprint fixture component lives
// in the Assets runtime fixture assembly (OpWeaveFixtures.Runtime) —
// AddComponent refuses Editor-assembly scripts, so the Editor test
// assembly cannot host it and must not reference an Assets asmdef. The
// type is therefore resolved by name and its fields are written through
// SerializedObject/SerializedProperty. A missing type fails the test
// with a clear message.
using System;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>Reflection access to the OpWeave.Fixtures prefab-fingerprint fixture types.</summary>
    internal static class FingerprintFixtureAccess
    {
        private const string FixtureAssembly = "OpWeaveFixtures.Runtime";
        private const string FixtureNamespace = "OpWeave.Fixtures";
        private const string BehaviourName = "FingerprintFixtureBehaviour";

        internal static Type FixtureType(string name)
        {
            var type = Type.GetType(
                FixtureNamespace + "." + name + ", " + FixtureAssembly);
            if (type is null)
            {
                throw new InvalidOperationException(
                    "the prefab fingerprint fixture type '" + FixtureNamespace +
                    "." + name + "' was not found in assembly '" + FixtureAssembly +
                    "'; TestProject/Assets/OpWeaveFixtures/Runtime must provide it");
            }
            return type;
        }

        internal static Component AddBehaviour(GameObject host)
        {
            var component = host.AddComponent(FixtureType(BehaviourName));
            if (component == null)
            {
                throw new InvalidOperationException(
                    "the fixture component '" + FixtureNamespace + "." + BehaviourName +
                    "' could not be attached to '" + host.name + "'");
            }
            return component;
        }

        internal static Component Behaviour(GameObject host)
        {
            var component = host.GetComponent(FixtureType(BehaviourName));
            if (component == null)
            {
                throw new InvalidOperationException(
                    "'" + host.name + "' carries no '" + FixtureNamespace + "." +
                    BehaviourName + "' fixture component");
            }
            return component;
        }

        // Creates a fixture ScriptableObject for the external-reference
        // fixtures. The type has a same-named script file, which the bare
        // ScriptableObject base class lacks ("No script asset").
        internal static ScriptableObject NewLinkedAsset() =>
            ScriptableObject.CreateInstance(FixtureType("FingerprintLinkedAsset"));

        // Constructs a managed-reference fixture object and fills its
        // serialized fields; assigned to a [SerializeReference] property
        // through SerializedProperty.managedReferenceValue.
        internal static object NewManagedImpl(int tag, float amount)
        {
            var type = FixtureType("FingerprintManagedImpl");
            var instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                throw new InvalidOperationException(
                    "Activator could not construct '" + type.FullName + "'");
            }
            SetField(instance, "tag", tag);
            SetField(instance, "amount", amount);
            return instance;
        }

        internal static void Edit(Component component, Action<SerializedObject> edit)
        {
            var serialized = new SerializedObject(component);
            edit(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name);
            if (field is null)
            {
                throw new InvalidOperationException(
                    "the fixture type '" + target.GetType().FullName +
                    "' has no field '" + name + "'");
            }
            field.SetValue(target, value);
        }
    }
}
