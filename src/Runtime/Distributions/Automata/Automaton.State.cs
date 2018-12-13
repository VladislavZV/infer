// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.ML.Probabilistic.Distributions.Automata
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Text;

    using Microsoft.ML.Probabilistic.Collections;
    using Microsoft.ML.Probabilistic.Core.Collections;
    using Microsoft.ML.Probabilistic.Distributions;
    using Microsoft.ML.Probabilistic.Math;
    using Microsoft.ML.Probabilistic.Serialization;
    using Microsoft.ML.Probabilistic.Utilities;

    public abstract partial class Automaton<TSequence, TElement, TElementDistribution, TSequenceManipulator, TThis>
        where TSequence : class, IEnumerable<TElement>
        where TElementDistribution : IDistribution<TElement>, SettableToProduct<TElementDistribution>, SettableToWeightedSumExact<TElementDistribution>, CanGetLogAverageOf<TElementDistribution>, SettableToPartialUniform<TElementDistribution>, new()
        where TSequenceManipulator : ISequenceManipulator<TSequence, TElement>, new()
        where TThis : Automaton<TSequence, TElement, TElementDistribution, TSequenceManipulator, TThis>, new()
    {
        /// <summary>
        /// Represents a reference to a state of automaton for exposure in public API.
        /// </summary>
        /// <remarks>
        /// Acts as a "fat reference" to state in automaton. In addition to reference to actual StateData it carries
        /// 2 additional properties for convinience: <see cref="Owner"/> automaton and <see cref="Index"/> of the state.
        /// We don't store them in <see cref="StateData"/> to save some memoty. C# compiler and .NET jitter are good
        /// at optimizing wrapping where it is not needed.
        /// </remarks>
        public struct State : IEquatable<State>
        {
            private readonly ReadOnlyArray<StateData> states;

            private readonly ReadOnlyArray<Transition> transitions;

            /// <summary>
            /// Initializes a new instance of <see cref="State"/> class. Used internally by automaton implementation
            /// to wrap StateData for use in public Automaton APIs.
            /// </summary>
            internal State(
                Automaton<TSequence, TElement, TElementDistribution, TSequenceManipulator, TThis> owner,
                ReadOnlyArray<StateData> states,
                ReadOnlyArray<Transition> transitions,
                int index)
            {
                this.Owner = owner;
                this.states = states;
                this.transitions = transitions;
                this.Index = index;
            }

/*
            /// <summary>
            /// Initializes a new instance of the <see cref="State"/> class. Created state does not belong
            /// to any automaton and has to be added to some automaton explicitly via Automaton.AddStates.
            /// </summary>
            /// <param name="index">The index of the state.</param>
            /// <param name="transitions">The outgoing transitions.</param>
            /// <param name="endWeight">The ending weight of the state.</param>
            [Construction("Index", "GetTransitions", "EndWeight")]
            public State(int index, IEnumerable<Transition> transitions, Weight endWeight)
                : this()
            {
                throw new NotImplementedException();
                Argument.CheckIfInRange(index >= 0, "index", "State index must be non-negative.");
                this.Index = index;
                this.states[this.Index] = new StateData(transitions, EndWeight);
            }
*/

            /// <summary>
            /// Automaton to which this state belongs.
            /// </summary>
            public Automaton<TSequence, TElement, TElementDistribution, TSequenceManipulator, TThis> Owner { get; }

            /// <summary>
            /// Gets the index of the state.
            /// </summary>
            public int Index { get; }

            /// <summary>
            /// Gets or sets the ending weight of the state.
            /// </summary>
            public Weight EndWeight => this.Data.EndWeight;
            
            /// <summary>
            /// Gets a value indicating whether the ending weight of this state is greater than zero.
            /// </summary>
            public bool CanEnd => this.Data.CanEnd;

            public ReadOnlyArrayView<Transition> Transitions =>
                new ReadOnlyArrayView<Transition>(
                    this.transitions,
                    this.Data.FirstTransition,
                    this.Data.LastTransition);

            internal StateData Data => this.states[this.Index];

            /// <summary>
            /// Compares 2 states for equality.
            /// </summary>
            public static bool operator ==(State a, State b) =>
                ReferenceEquals(a.Owner, b.Owner) && a.Index == b.Index;

            /// <summary>
            /// Compares 2 states for inequality.
            /// </summary>
            public static bool operator !=(State a, State b) => !(a == b);

            /// <summary>
            /// Compares 2 states for equality.
            /// </summary>
            public bool Equals(State that) => this == that;

            /// <summary>
            /// Compares 2 states for equality.
            /// </summary>
            public override bool Equals(object obj) => obj is State that && this.Equals(that);

            /// <summary>
            /// Returns HashCode of this state.
            /// </summary>
            public override int GetHashCode() => this.Data.GetHashCode();

            /// <summary>
            /// Returns a string that represents the state.
            /// </summary>
            /// <returns>A string that represents the state.</returns>
            public override string ToString()
            {
                const string StartStateMarker = "START ->";
                const string TransitionSeparator = ",";

                var sb = new StringBuilder();

                bool isStartState = this.Owner != null && this.Owner.Start == this;
                if (isStartState)
                {
                    sb.Append(StartStateMarker);
                }

                bool firstTransition = true;
                foreach (var transition in this.Transitions)
                {
                    if (firstTransition)
                    {
                        firstTransition = false;
                    }
                    else
                    {
                        sb.Append(TransitionSeparator);
                    }

                    sb.Append(transition.ToString());
                }

                if (CanEnd)
                {
                    if (!firstTransition) sb.Append(TransitionSeparator);
                    sb.Append(this.EndWeight.Value + " -> END");
                }

                return sb.ToString();
            }

            /// <summary>
            /// Computes the logarithm of the value of the automaton
            /// having this state as the start state on a given sequence.
            /// </summary>
            /// <param name="sequence">The sequence.</param>
            /// <returns>The logarithm of the value on the sequence.</returns>
            public double GetLogValue(TSequence sequence)
            {
                var valueCache = new Dictionary<(int, int), Weight>();
                return this.DoGetValue(sequence, 0, valueCache).LogValue;
            }

            /// <summary>
            /// Gets whether the automaton having this state as the start state is zero everywhere.
            /// </summary>
            /// <returns>A value indicating whether the automaton having this state as the start state is zero everywhere.</returns>
            public bool IsZero()
            {
                var visitedStates = new BitArray(this.Owner.States.Count, false);
                return this.DoIsZero(visitedStates);
            }

            /// <summary>
            /// Gets whether the automaton having this state as the start state has non-trivial loops.
            /// </summary>
            /// <returns>A value indicating whether the automaton having this state as the start state is zero everywhere.</returns>
            public bool HasNonTrivialLoops()
            {
                return this.DoHasNonTrivialLoops(new ArrayDictionary<bool>(this.Owner.States.Count));
            }

            /// <summary>
            /// Gets the epsilon closure of this state.
            /// </summary>
            /// <returns>The epsilon closure of this state.</returns>
            public EpsilonClosure GetEpsilonClosure()
            {
                return new EpsilonClosure(this);
            }

            #region Helpers

            /// <summary>
            /// Recursively checks if the automaton has non-trivial loops
            /// (i.e. loops consisting of more than one transition).
            /// </summary>
            /// <param name="stateInStack">
            /// A dictionary, storing for each state whether it has already been visited, and,
            /// if so, whether the state still is on the traversal stack.</param>
            /// <returns>
            /// <see langword="true"/> if a non-trivial loop has been found,
            /// <see langword="false"/> otherwise.
            /// </returns>
            private bool DoHasNonTrivialLoops(ArrayDictionary<bool> stateInStack)
            {
                bool inStack;
                if (stateInStack.TryGetValue(this.Index, out inStack))
                {
                    return inStack;
                }

                stateInStack.Add(this.Index, true);

                foreach (var transition in this.Transitions)
                {
                    if (transition.DestinationStateIndex != this.Index)
                    {
                        var destState = this.Owner.States[transition.DestinationStateIndex];
                        if (destState.DoHasNonTrivialLoops(stateInStack))
                        {
                            return true;
                        }
                    }
                }

                stateInStack[this.Index] = false;
                return false;
            }

            /// <summary>
            /// Recursively checks if the automaton is zero.
            /// </summary>
            /// <param name="visitedStates">For each state stores whether it has been already visited.</param>
            /// <returns>
            /// <see langword="false"/> if an accepting path has been found,
            /// <see langword="true"/> otherwise.
            /// </returns>
            private bool DoIsZero(BitArray visitedStates)
            {
                if (visitedStates[this.Index])
                {
                    return true;
                }

                visitedStates[this.Index] = true;

                var isZero = !this.CanEnd;
                var transitionIndex = 0;
                while (isZero && transitionIndex < this.Transitions.Count)
                {
                    var transition = this.Transitions[transitionIndex];
                    if (!transition.Weight.IsZero)
                    {
                        var destState = this.Owner.States[transition.DestinationStateIndex];
                        isZero = destState.DoIsZero(visitedStates);
                    }

                    ++transitionIndex;
                }

                return isZero;
            }

            /// <summary>
            /// Recursively computes the value of the automaton on a given sequence.
            /// </summary>
            /// <param name="sequence">The sequence to compute the value on.</param>
            /// <param name="sequencePosition">The current position in the sequence.</param>
            /// <param name="valueCache">A lookup table for memoization.</param>
            /// <returns>The value computed from the current state.</returns>
            private Weight DoGetValue(
                TSequence sequence, int sequencePosition, Dictionary<(int, int), Weight> valueCache)
            {
                var stateIndexPair = (this.Index, sequencePosition);
                if (valueCache.TryGetValue(stateIndexPair, out var cachedValue))
                {
                    return cachedValue;
                }

                var closure = this.GetEpsilonClosure();

                var value = Weight.Zero;
                var count = SequenceManipulator.GetLength(sequence);
                var isCurrent = sequencePosition < count;
                if (isCurrent)
                {
                    var element = SequenceManipulator.GetElement(sequence, sequencePosition);
                    for (var closureStateIndex = 0; closureStateIndex < closure.Size; ++closureStateIndex)
                    {
                        var closureState = closure.GetStateByIndex(closureStateIndex);
                        var closureStateWeight = closure.GetStateWeightByIndex(closureStateIndex);

                        foreach (var transition in closureState.Transitions)
                        {
                            if (transition.IsEpsilon)
                            {
                                continue; // The destination is a part of the closure anyway
                            }

                            var destState = this.Owner.States[transition.DestinationStateIndex];
                            var distWeight = Weight.FromLogValue(transition.ElementDistribution.Value.GetLogProb(element));
                            if (!distWeight.IsZero && !transition.Weight.IsZero)
                            {
                                var destValue = destState.DoGetValue(sequence, sequencePosition + 1, valueCache);
                                if (!destValue.IsZero)
                                {
                                    value = Weight.Sum(
                                        value,
                                        Weight.Product(closureStateWeight, transition.Weight, distWeight, destValue));
                                }
                            }
                        }
                    }
                }
                else
                {
                    value = closure.EndWeight;
                }

                valueCache.Add(stateIndexPair, value);
                return value;
            }

            #endregion

            /// <summary>
            /// Whether there are incoming transitions to this state
            /// </summary>
            public bool HasIncomingTransitions
            {
                // TODO: remove?
                get
                {
                    var this_ = this;
                    return this.Owner.States.Any(
                        state => state.Transitions.Any(
                            transition => transition.DestinationStateIndex == this_.Index));
                }
            }

            #region Serialization

            public void Write(Action<double> writeDouble, Action<int> writeInt32, Action<TElementDistribution> writeElementDistribution)
            {
                this.EndWeight.Write(writeDouble);
                writeInt32(this.Index);
                writeInt32(this.Transitions.Count);
                foreach (var transition in this.Transitions)
                {
                    transition.Write(writeInt32, writeDouble, writeElementDistribution);
                }
            }

            /// <summary>
            /// Reads state and appends it into Automaton builder. Returns index in the serialized data.
            /// If <paramref name="checkIndex"/> is true, will throw exception if serialized index
            /// does not match index in deserialized states array. This check is bypassed only when
            /// start state is serialized second time.
            /// </summary>
            public static int ReadTo(
                ref Builder builder,
                Func<int> readInt32,
                Func<double> readDouble,
                Func<TElementDistribution> readElementDistribution,
                bool checkIndex = false)
            {
                var endWeight = Weight.Read(readDouble);
                // Note: index is serialized for compatibility with old binary serializations
                var index = readInt32();

                if (checkIndex && index != builder.StatesCount)
                {
                    throw new Exception("Index in serialized data does not match index in deserialized array");
                }

                var state = builder.AddState();
                state.SetEndWeight(endWeight);

                var transitionCount = readInt32();
                for (var i = 0; i < transitionCount; i++)
                {
                    state.AddTransition(Transition.Read(readInt32, readDouble, readElementDistribution));
                }

                return index;
            }

            #endregion
        }
    }
}
