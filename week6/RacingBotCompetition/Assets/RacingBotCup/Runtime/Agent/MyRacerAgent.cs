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
        [SerializeField] float m_ProgressReward = 0.06f;

        [Tooltip("Charged every decision, so standing still is never comfortable.")]
        [SerializeField] float m_TimePenalty = 0.001f;

        [Tooltip("Charged while any part of the car is off the racing surface.")]
        [SerializeField] float m_OffTrackPenalty = 0.01f;

        [SerializeField] float m_LapBonus = 20f;

        [SerializeField] float m_FailurePenalty = 3f;

        [Tooltip("직선 판정 곡률 상한")]
        [SerializeField] float m_CurvatureFullCurve = 0.05f;

        [Tooltip("직선 판정 구간(m)")]
        [SerializeField] float m_SteerCurvatureWindow = 15f;

        [Tooltip("직선 판정 시 몇 m 앞을 볼지 (코너 진입 전 미리 억제 해제)")]
        [SerializeField] float m_SteerLookahead = 12f;

        [Tooltip("직선 조향 페널티 가중치")]
        [SerializeField] float m_SteerPenaltyOnStraight = 0.05f;

        [Tooltip("이 이하의 조향은 보정으로 간주해 페널티 면제")]
        [SerializeField] float m_SteerDeadZone = 0.1f;

        [Tooltip("직선에서 후진 페널티")]
        [SerializeField] float m_ReverseOnStraightPenalty = 0.05f;

        [Tooltip("직선에서 헤딩 정렬 페널티 가중치")]
        [SerializeField] float m_HeadingPenaltyOnStraight = 0.08f;

        [Tooltip("이 이하의 헤딩 오차(0~1)는 보정으로 간주해 페널티 면제")]
        [SerializeField] float m_HeadingDeadZone = 0.05f;

        [Tooltip("직선에서 최고속 유지 보상 가중치")]
        [SerializeField] float m_TopSpeedOnStraightReward = 0.01f;

        float m_BestProgress;

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

            // 최고 도달 지점 기준 — 뒤로 갔다 다시 그 자리를 밟아도 중복 보상 없음, 왕복해도 손해도 이득도 없음
            // Progress는 0~1 비율이라, 기존 m_ProgressReward(미터당 계수) 스케일을 유지하려고 트랙 길이를 다시 곱함
            // 트랙 밖에서 번 진행거리는 보상하지 않음 (컷 방지, 이중보상 방지 위해 갱신은 그대로)
            var progress = Checkpoints.Progress;
            if (progress > m_BestProgress)
            {
                var metresGained = (progress - m_BestProgress) * Track.TotalLength;
                if (!IsOffTrack)
                {
                    AddReward(metresGained * m_ProgressReward);
                }
                m_BestProgress = progress;
            }

            AddReward(-m_TimePenalty);

            if (IsOffTrack)
            {
                AddReward(-m_OffTrackPenalty);
            }

            // 직선 조향 억제 (데드존 이하 보정 조향은 면제)
            var curvature = Mathf.Abs(CurvatureAhead(m_SteerLookahead, m_SteerCurvatureWindow));
            var straightness = Mathf.InverseLerp(m_CurvatureFullCurve, 0f, curvature);
            var steerPenalty = Mathf.Max(0f, Mathf.Abs(steer) - m_SteerDeadZone);
            AddReward(-steerPenalty * straightness * m_SteerPenaltyOnStraight);

            // 직선에서 후진 페널티 (코너에서는 straightness가 0이라 개입 안 함)
            if (Car.ForwardSpeed < 0f)
            {
                AddReward(-straightness * m_ReverseOnStraightPenalty);
            }

            // 직선에서 헤딩(진행방향)이 트랙과 어긋난 만큼 페널티 (데드존 이하 보정은 면제)
            var headingErrorRaw = Mathf.Abs(Vector3.SignedAngle(Projection.Forward, Car.transform.forward, Vector3.up)) / 90f;
            var headingError = Mathf.Max(0f, headingErrorRaw - m_HeadingDeadZone);
            AddReward(-headingError * straightness * m_HeadingPenaltyOnStraight);

            // 직선에서 최고속에 가까울수록 보상 (트랙 밖이면 지급 안 함, 관측과 동일하게 50f 기준 정규화)
            if (!IsOffTrack)
            {
                var speedFraction = Mathf.Clamp01(Car.ForwardSpeed / 50f);
                AddReward(speedFraction * straightness * m_TopSpeedOnStraightReward);
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
            m_BestProgress = 0f;
            base.OnEpisodeBegin();
        }

    }
}
