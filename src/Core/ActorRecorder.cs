using System.Collections.Generic;
using UnityEngine;

namespace Crawlspace2MP
{
    /// <summary>
    /// Records and plays back local player motion as "actors" using the RemotePlayer model.
    /// Used for trailer/content creation in debug mode (F4).
    /// </summary>
    public class ActorRecorder
    {
        public struct Frame
        {
            public Vector3 HeadPos;
            public Quaternion HeadRot;
            public Vector3 LeftHandPos;
            public Quaternion LeftHandRot;
            public Vector3 RightHandPos;
            public Quaternion RightHandRot;
            public float LeftGrip;
            public float LeftTrigger;
            public float RightGrip;
            public float RightTrigger;
        }

        public class Recording
        {
            public List<Frame> Frames = new List<Frame>();
            public string Name;
            public float FrameInterval; // seconds between frames
        }

        public class Actor
        {
            public string Name;
            public Recording Recording;
            public RemotePlayer Visual;
            public int CurrentFrame;
            public float FrameTimer;
            public bool IsPlaying;
            public bool IsLooping;
        }

        // State
        public bool IsRecording { get; private set; }
        private Recording _currentRecording;
        private float _recordTimer;
        private const float RECORD_INTERVAL = 0.033f; // ~30fps

        // All saved recordings
        public List<Recording> Recordings { get; } = new List<Recording>();

        // All spawned actors
        public List<Actor> Actors { get; } = new List<Actor>();

        private int _nextActorId = 1;

        /// <summary>
        /// Start recording the local player's motion.
        /// </summary>
        public void StartRecording()
        {
            _currentRecording = new Recording
            {
                Name = $"Recording {Recordings.Count + 1}",
                FrameInterval = RECORD_INTERVAL,
                Frames = new List<Frame>()
            };
            _recordTimer = 0f;
            IsRecording = true;
        }

        /// <summary>
        /// Stop recording and save.
        /// </summary>
        public void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;

            if (_currentRecording.Frames.Count > 0)
            {
                Recordings.Add(_currentRecording);
            }
            _currentRecording = null;
        }

        /// <summary>
        /// Capture a frame from the local player. Called from Update when recording.
        /// </summary>
        public void CaptureFrame()
        {
            if (!IsRecording || _currentRecording == null) return;

            _recordTimer += Time.deltaTime;
            if (_recordTimer < RECORD_INTERVAL) return;
            _recordTimer -= RECORD_INTERVAL;

            // Get local player tracking data (same sources as SendPositionUpdate)
            Vector3 headPos, leftHandPos, rightHandPos;
            Quaternion headRot, leftHandRot, rightHandRot;

            var backpack = Object.FindObjectOfType<BackpackControl>();
            Camera cam = Camera.main;

            // Try BackpackControl references first
            Transform ovrHead = null, ovrLeft = null, ovrRight = null;
            if (backpack != null)
            {
                if (backpack.cam != null) ovrHead = backpack.cam.transform;
                if (backpack.leftHand != null) ovrLeft = backpack.leftHand.transform;
                if (backpack.rightHand != null) ovrRight = backpack.rightHand.transform;
            }

            if (ovrHead != null && ovrLeft != null && ovrRight != null)
            {
                headPos = ovrHead.position;
                headRot = ovrHead.rotation;
                leftHandPos = ovrLeft.position;
                leftHandRot = ovrLeft.rotation;
                rightHandPos = ovrRight.position;
                rightHandRot = ovrRight.rotation;
            }
            else if (cam != null)
            {
                headPos = cam.transform.position;
                headRot = cam.transform.rotation;
                leftHandPos = headPos + cam.transform.right * -0.3f + cam.transform.forward * 0.2f;
                leftHandRot = headRot;
                rightHandPos = headPos + cam.transform.right * 0.3f + cam.transform.forward * 0.2f;
                rightHandRot = headRot;
            }
            else
            {
                return; // No tracking source
            }

            // Capture hand poses
            float leftGrip = 0f, leftTrigger = 0f, rightGrip = 0f, rightTrigger = 0f;
            if (backpack != null)
            {
                try
                {
                    if (backpack.controllerLeft != null)
                    {
                        leftGrip = backpack.controllerLeft.selectActionValue.action.ReadValue<float>();
                        leftTrigger = backpack.controllerLeft.uiPressActionValue.action.ReadValue<float>();
                    }
                    if (backpack.controllerRight != null)
                    {
                        rightGrip = backpack.controllerRight.selectActionValue.action.ReadValue<float>();
                        rightTrigger = backpack.controllerRight.uiPressActionValue.action.ReadValue<float>();
                    }
                }
                catch { }
            }

            _currentRecording.Frames.Add(new Frame
            {
                HeadPos = headPos,
                HeadRot = headRot,
                LeftHandPos = leftHandPos,
                LeftHandRot = leftHandRot,
                RightHandPos = rightHandPos,
                RightHandRot = rightHandRot,
                LeftGrip = leftGrip,
                LeftTrigger = leftTrigger,
                RightGrip = rightGrip,
                RightTrigger = rightTrigger
            });
        }

        /// <summary>
        /// Spawn an actor that plays back a recording.
        /// </summary>
        public Actor SpawnActor(Recording recording, bool loop = true)
        {
            // Create a RemotePlayer visual with a fake peer ID
            int fakePeerId = -100 - _nextActorId;
            var visual = new RemotePlayer(fakePeerId);

            var actor = new Actor
            {
                Name = $"Actor {_nextActorId} ({recording.Name})",
                Recording = recording,
                Visual = visual,
                CurrentFrame = 0,
                FrameTimer = 0f,
                IsPlaying = true,
                IsLooping = loop
            };

            _nextActorId++;
            Actors.Add(actor);

            // Apply first frame immediately
            if (recording.Frames.Count > 0)
            {
                ApplyFrame(actor, recording.Frames[0]);
            }

            return actor;
        }

        /// <summary>
        /// Remove an actor and destroy its visual.
        /// </summary>
        public void RemoveActor(Actor actor)
        {
            actor.Visual?.Destroy();
            Actors.Remove(actor);
        }

        /// <summary>
        /// Remove all actors.
        /// </summary>
        public void RemoveAllActors()
        {
            for (int i = Actors.Count - 1; i >= 0; i--)
            {
                Actors[i].Visual?.Destroy();
            }
            Actors.Clear();
        }

        /// <summary>
        /// Delete a saved recording (and any actors using it).
        /// </summary>
        public void DeleteRecording(Recording recording)
        {
            // Remove any actors using this recording
            for (int i = Actors.Count - 1; i >= 0; i--)
            {
                if (Actors[i].Recording == recording)
                {
                    Actors[i].Visual?.Destroy();
                    Actors.RemoveAt(i);
                }
            }
            Recordings.Remove(recording);
        }

        /// <summary>
        /// Toggle play/pause on an actor.
        /// </summary>
        public void ToggleActor(Actor actor)
        {
            actor.IsPlaying = !actor.IsPlaying;
            if (actor.IsPlaying)
            {
                actor.CurrentFrame = 0;
                actor.FrameTimer = 0f;
            }
        }

        /// <summary>
        /// Update all playing actors. Called from MPManager.Update.
        /// </summary>
        public void Update()
        {
            foreach (var actor in Actors)
            {
                if (!actor.IsPlaying || actor.Recording.Frames.Count == 0) continue;

                actor.FrameTimer += Time.deltaTime;
                if (actor.FrameTimer >= actor.Recording.FrameInterval)
                {
                    actor.FrameTimer -= actor.Recording.FrameInterval;
                    actor.CurrentFrame++;

                    if (actor.CurrentFrame >= actor.Recording.Frames.Count)
                    {
                        if (actor.IsLooping)
                        {
                            actor.CurrentFrame = 0;
                        }
                        else
                        {
                            actor.CurrentFrame = actor.Recording.Frames.Count - 1;
                            actor.IsPlaying = false;
                            continue;
                        }
                    }
                }

                // Interpolate between current and next frame for smooth playback
                int nextFrame = (actor.CurrentFrame + 1) % actor.Recording.Frames.Count;
                float t = actor.FrameTimer / actor.Recording.FrameInterval;
                var a = actor.Recording.Frames[actor.CurrentFrame];
                var b = actor.Recording.Frames[nextFrame];

                var interpolated = new Frame
                {
                    HeadPos = Vector3.Lerp(a.HeadPos, b.HeadPos, t),
                    HeadRot = Quaternion.Slerp(a.HeadRot, b.HeadRot, t),
                    LeftHandPos = Vector3.Lerp(a.LeftHandPos, b.LeftHandPos, t),
                    LeftHandRot = Quaternion.Slerp(a.LeftHandRot, b.LeftHandRot, t),
                    RightHandPos = Vector3.Lerp(a.RightHandPos, b.RightHandPos, t),
                    RightHandRot = Quaternion.Slerp(a.RightHandRot, b.RightHandRot, t),
                    LeftGrip = Mathf.Lerp(a.LeftGrip, b.LeftGrip, t),
                    LeftTrigger = Mathf.Lerp(a.LeftTrigger, b.LeftTrigger, t),
                    RightGrip = Mathf.Lerp(a.RightGrip, b.RightGrip, t),
                    RightTrigger = Mathf.Lerp(a.RightTrigger, b.RightTrigger, t)
                };

                ApplyFrame(actor, interpolated);
            }
        }

        private void ApplyFrame(Actor actor, Frame frame)
        {
            if (actor.Visual == null) return;

            Vector3 bodyPos = frame.HeadPos - Vector3.up * 0.5f;
            Quaternion bodyRot = Quaternion.Euler(0, frame.HeadRot.eulerAngles.y, 0);

            actor.Visual.SetTargets(
                true,
                bodyPos, bodyRot,
                frame.HeadPos, frame.HeadRot,
                frame.LeftHandPos, frame.LeftHandRot,
                frame.RightHandPos, frame.RightHandRot,
                frame.LeftGrip, frame.LeftTrigger,
                frame.RightGrip, frame.RightTrigger
            );
            actor.Visual.UpdateInterpolation();
        }

        /// <summary>
        /// Clean up everything.
        /// </summary>
        public void Cleanup()
        {
            if (IsRecording) StopRecording();
            RemoveAllActors();
            Recordings.Clear();
        }
    }
}
