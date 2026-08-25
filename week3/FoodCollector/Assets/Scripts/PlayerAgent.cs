using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

[RequireComponent(typeof(Rigidbody))]
public class PlayerAgent : Agent
{
    [Tooltip("이동 힘 배율 (레퍼런스 moveSpeed)")]
    public float moveSpeed = 2f;
    [Tooltip("회전 속도 (deg/s, 레퍼런스 turnSpeed)")]
    public float turnSpeed = 300f;
    [Tooltip("애니메이션 재생 속도 배율 (1 = 원본)")]
    public float animationSpeed = 1f;

    Rigidbody rb;
    Animator animator;

    float m_Forward;
    float m_Rotate;

    FoodArea m_FoodArea;

    public float spawnRange = 4f;
    public float meatReward = 1f;
    public float carrotReward = -1f;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        m_FoodArea = GetComponentInParent<FoodArea>();
    }

    //에피소드 시작될때 위치, 회전, 음식 위치 리셋
    public override void OnEpisodeBegin()
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.localPosition = new Vector3(Random.Range(-spawnRange, spawnRange), 0.6f, Random.Range(-spawnRange, spawnRange));
        transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        m_FoodArea.ResetFoods();
    }

    // 행동 결정 호출
    public override void OnActionReceived(ActionBuffers actions)
    {
        m_Forward = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        m_Rotate  = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

        AddReward(-1 / MaxStep); // 시간 경과에 따른 작은 보상 (최대 스텝까지 버티도록 유도)
    }

    // 벡터 관측 정보 추가
    public override void CollectObservations(VectorSensor sensor)
    {
        var localVelocity = transform.InverseTransformDirection(rb.linearVelocity);
        sensor.AddObservation(localVelocity.x);
        sensor.AddObservation(localVelocity.z);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    ca[0] = 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  ca[0] = -1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) ca[1] = 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  ca[1] = -1f;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("food"))
        {
            AddReward(meatReward);
            other.GetComponent<Collectible>().Respawn();
        }
        else if (other.CompareTag("badFood"))
        {
            AddReward(carrotReward);
            other.GetComponent<Collectible>().Respawn();
        }
    }

    void Update()
    {
        SetAnimation();
    }

    void FixedUpdate()
    {
        MoveAgent();
    }

    void MoveAgent()
    {
        Vector3 dirToGo = transform.forward * m_Forward;
        Vector3 rotateDir = transform.up * m_Rotate;

        rb.AddForce(dirToGo * moveSpeed, ForceMode.VelocityChange);
        transform.Rotate(rotateDir, Time.fixedDeltaTime * turnSpeed);

        if (rb.linearVelocity.sqrMagnitude > 25f) // 최고 속도 제한
            rb.linearVelocity *= 0.95f;

        // 벽 접촉 마찰로 생기는 회전 누적 방지 (회전은 transform.Rotate로만 제어)
        rb.angularVelocity = Vector3.zero;
    }

    void SetAnimation()
    {
        if (animator == null) return;
        animator.speed = animationSpeed;
        Vector3 hv = rb.linearVelocity; hv.y = 0f;
        animator.SetFloat("Speed", hv.magnitude);
    }
}