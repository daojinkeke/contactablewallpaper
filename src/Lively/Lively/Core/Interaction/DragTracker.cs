using Lively.Models.Interaction;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Per button drag state machine, see docs/contracts/06-interaction-spec.md §8.
    /// Pure state holder: the router injects the down/up messages and replays the
    /// releases returned by <see cref="Reset"/>.
    /// </summary>
    public sealed class DragTracker
    {
        private readonly object syncRoot = new object();
        // Button pressed state; value is the input target the press was delivered to.
        private readonly Dictionary<InteractionPointerButton, IntPtr> pressedButtons = new Dictionary<InteractionPointerButton, IntPtr>();
        private Point lastPoint;

        /// <summary>
        /// Last known pointer position in display local physical pixels.
        /// </summary>
        public Point LastPoint
        {
            get
            {
                lock (syncRoot)
                {
                    return lastPoint;
                }
            }
        }

        public bool IsPressed(InteractionPointerButton button)
        {
            lock (syncRoot)
            {
                return pressedButtons.ContainsKey(button);
            }
        }

        /// <summary>
        /// Input target the press was delivered to, <see cref="IntPtr.Zero"/> when not pressed.
        /// </summary>
        public IntPtr GetPressedHandle(InteractionPointerButton button)
        {
            lock (syncRoot)
            {
                return pressedButtons.TryGetValue(button, out var handle) ? handle : IntPtr.Zero;
            }
        }

        public bool HasPressedButtons
        {
            get
            {
                lock (syncRoot)
                {
                    return pressedButtons.Count != 0;
                }
            }
        }

        /// <summary>
        /// Pressed buttons as the JS bridge <c>buttons</c> bitmask (1 left, 2 right, 4 middle, 0 none).
        /// </summary>
        public int ButtonsMask
        {
            get
            {
                lock (syncRoot)
                {
                    int mask = 0;
                    foreach (var button in pressedButtons.Keys)
                        mask |= (int)button;
                    return mask;
                }
            }
        }

        /// <summary>
        /// Records a pointer down. Returns false when the button is already pressed
        /// (duplicate down is ignored so down/up always pair up.)
        /// </summary>
        public bool Press(InteractionPointerButton button, Point localPoint, IntPtr targetHandle)
        {
            lock (syncRoot)
            {
                lastPoint = localPoint;
                if (button == InteractionPointerButton.none || pressedButtons.ContainsKey(button))
                    return false;

                pressedButtons.Add(button, targetHandle);
                return true;
            }
        }

        /// <summary>
        /// Records a pointer up. Returns false when the button was not pressed
        /// (stray up, nothing to inject.)
        /// </summary>
        public bool Release(InteractionPointerButton button, Point localPoint)
        {
            lock (syncRoot)
            {
                lastPoint = localPoint;
                return pressedButtons.Remove(button);
            }
        }

        public void Move(Point localPoint)
        {
            lock (syncRoot)
            {
                lastPoint = localPoint;
            }
        }

        /// <summary>
        /// Releases all pressed buttons (focus loss, wallpaper switch, display change,
        /// Explorer restart, lock/unlock) and clears the buttons mask.
        /// Returned releases must be injected as pointer up at the last known position.
        /// </summary>
        public IReadOnlyList<DragRelease> Reset()
        {
            lock (syncRoot)
            {
                if (pressedButtons.Count == 0)
                    return Array.Empty<DragRelease>();

                var releases = new DragRelease[pressedButtons.Count];
                int index = 0;
                foreach (var item in pressedButtons)
                {
                    releases[index++] = new DragRelease(item.Key, item.Value, lastPoint);
                }

                pressedButtons.Clear();
                return releases;
            }
        }
    }

    /// <summary>
    /// Pointer up to replay on <see cref="DragTracker.Reset"/>.
    /// </summary>
    public readonly struct DragRelease
    {
        public InteractionPointerButton Button { get; }
        public IntPtr TargetHandle { get; }
        public Point Point { get; }

        public DragRelease(InteractionPointerButton button, IntPtr targetHandle, Point point)
        {
            Button = button;
            TargetHandle = targetHandle;
            Point = point;
        }
    }
}
