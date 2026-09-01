using Unity.MLAgents.Sensors;
using UnityEngine;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// A worked example of a competition entry. Copy this file, rename the class, and change
    /// whatever you like — this is a starting point, not a recommendation.
    ///
    /// It shows the two things you are expected to design (기획서 §3):
    /// <list type="number">
    /// <item><b>Observations</b> — what the policy is allowed to see, in
    /// <see cref="CollectObservations"/> plus whatever sensor components you add in the Inspector.</item>
    /// <item><b>Rewards</b> — what counts as doing well, in <see cref="OnDriveApplied"/> and the
    /// two episode hooks. Rewards exist only while training and never touch your score.</item>
    /// </list>
    ///
    /// Everything read below comes from <see cref="RacerAgent"/>. Values are scaled to roughly
    /// -1..1 by hand here; a policy learns much faster when its inputs are on a similar scale, and
    /// raw metres per second next to a 0-to-1 lap fraction is not.
    /// </summary>
    public class MyRacerAgent : RacerAgent
    {
        [Header("Observation shape")]
        [Tooltip("How many centreline points ahead to look at.")]
        [SerializeField] int m_Waypoints = 5;

        [Tooltip("Metres between those points.")]
        [SerializeField] float m_WaypointSpacing = 10f;

        [Tooltip("How many curvature windows ahead to sample.")]
        [SerializeField] int m_CurvatureWindows = 3;

        [SerializeField] float m_CurvatureWindowLength = 20f;

        [Header("Reward shaping")]
        [Tooltip("Per metre of forward progress along the circuit.")]
        [SerializeField] float m_ProgressReward = 0.02f;

        [Tooltip("Charged every decision, so standing still is never comfortable.")]
        [SerializeField] float m_TimePenalty = 0.001f;

        [SerializeField] float m_LapBonus = 20f;

        [SerializeField] float m_FailurePenalty = 3f;

        [Tooltip("진전 여부를 확인할 결정 구간 길이")]
        [SerializeField] int m_StuckWindowDecisions = 25;

        [Tooltip("이 창 동안 순변위가 이 거리(m) 미만이면 정지로 간주")]
        [SerializeField] float m_StuckDistanceThreshold = 1f;

        [Tooltip("정지 지속 페널티 (정지로 판정된 창 동안 매 틱 반복 부과)")]
        [SerializeField] float m_StuckPenalty = 0.03f;

        float m_LastProgress;
        float m_WindowStartProgress;
        int m_WindowDecisionCount;
        bool m_IsStuck;

        /// <summary>
        /// Total floats written below. Put this number in BehaviorParameters →
        /// Vector Observation → Space Size, or the policy and the observations disagree and
        /// ML-Agents throws on the first decision.
        /// </summary>
        public int ObservationSize => 8 + m_Waypoints * 2 + m_CurvatureWindows;

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!IsBound)
            {
                // Bound by the harness before the first decision; this guard only matters if you
                // drop the agent into a scene by hand.
                for (var i = 0; i < ObservationSize; i++)
                {
                    sensor.AddObservation(0f);
                }

                return;
            }

            // --- how the car is moving (5 floats) ---
            sensor.AddObservation(Car.ForwardSpeed / 50f);
            sensor.AddObservation(Car.LocalVelocity.x / 50f);
            sensor.AddObservation(Car.LocalAngularVelocity.y / 5f);
            sensor.AddObservation(Car.SteerAngleNormalized);
            sensor.AddObservation(Car.SlipAngle / 45f);

            // --- where it is on the circuit (3 floats) ---
            var projection = Projection;
            var halfWidth = Mathf.Max(0.5f, projection.Width * 0.5f);
            sensor.AddObservation(projection.Lateral / halfWidth);   // ±1 at the road edges
            sensor.AddObservation(projection.Width / 12f);
            sensor.AddObservation(IsOffTrack ? 1f : 0f);

            // --- what is coming up (m_Waypoints * 2 + m_CurvatureWindows floats) ---
            for (var i = 1; i <= m_Waypoints; i++)
            {
                var local = WaypointLocal(i * m_WaypointSpacing);
                sensor.AddObservation(local.x / 50f);
                sensor.AddObservation(local.z / 50f);
            }

            for (var i = 0; i < m_CurvatureWindows; i++)
            {
                var curvature = CurvatureAhead(i * m_CurvatureWindowLength, m_CurvatureWindowLength);
                sensor.AddObservation(Mathf.Clamp(curvature * 30f, -3f, 3f));
            }
        }

        // ------------------------------------------------------------------
        // 보상 설계 — 여기서부터가 여러분의 영역입니다.
        // ------------------------------------------------------------------

        protected override void OnDriveApplied(float steer, float throttle)
        {
            if (!IsBound)
            {
                return;
            }

            // Progress along the centreline is the primary signal. Measured as a delta so the agent
            // is paid for advancing, not for being far along.
            var progress = Checkpoints.TraveledDistance;
            AddReward((progress - m_LastProgress) * m_ProgressReward);
            m_LastProgress = progress;

            AddReward(-m_TimePenalty);

            // 정지 지속 시간 추적, 일정 시간 이상 멈춰있으면 짧은 창 동안만 후진+조향을 살짝 유도
            m_WindowDecisionCount++;
            if (m_WindowDecisionCount >= m_StuckWindowDecisions)
            {
                var netMoved = Mathf.Abs(progress - m_WindowStartProgress);
                m_IsStuck = netMoved < m_StuckDistanceThreshold;
                m_WindowStartProgress = progress;
                m_WindowDecisionCount = 0;
            }

            if (m_IsStuck)
            {
                AddReward(-m_StuckPenalty);
            }
        }

        public override void OnLapCompleted(float elapsedSeconds)
        {
            // Finishing is worth a lot, finishing quickly a little more.
            AddReward(Mathf.Max(1f, m_LapBonus - elapsedSeconds * 0.05f));
        }

        public override void OnRunFailed()
        {
            AddReward(-m_FailurePenalty);
        }

        public override void OnEpisodeBegin()
        {
            m_LastProgress = 0f;
            m_WindowStartProgress = 0f;
            m_WindowDecisionCount = 0;
            m_IsStuck = false;
            base.OnEpisodeBegin();
        }

    }
}
