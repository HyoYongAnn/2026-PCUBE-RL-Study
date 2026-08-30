using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Tetris
{
    /// <summary>
    /// TetrisCore 로직을 스프라이트 셀 그리드로 렌더링한다. 두 가지 모드:
    ///  - autoPlay = true : 자체 TetrisCore 를 만들어 키보드(New Input System)로 사람이 플레이(로직 검증용).
    ///  - autoPlay = false: Bind(core) 로 받은 외부 코어(예: 에이전트의 env.Core)를 렌더만 한다(학습 관전용).
    /// </summary>
    public class TetrisBoard : MonoBehaviour
    {
        [Header("블록 스프라이트 (인덱스 = PieceType: I,O,T,S,Z,J,L)")]
        public Sprite[] blockSprites = new Sprite[7];

        [Header("설정")]
        public int seed = 0;
        public float cellSize = 1f;
        public Color ghostColor = new Color(1f, 1f, 1f, 0.25f);
        public Color emptyColor = new Color(1f, 1f, 1f, 0.06f);

        [Header("모드")]
        [Tooltip("true: 자체 코어로 사람이 직접 플레이. false: Bind(core)로 받은 외부 코어를 렌더만 함(학습 관전).")]
        public bool autoPlay = true;

        [Tooltip("HUD 상단에 표시할 상태 문구(예: INFERENCE). 비우면 표시 안 함. 에이전트가 설정.")]
        public string statusLabel = "";

        [Header("입력 반복(초)")]
        public float dasDelay = 0.15f;    // 첫 반복까지 지연
        public float arrRate = 0.04f;     // 반복 간격

        TetrisCore game;
        SpriteRenderer[,] cells;          // 보드 셀
        SpriteRenderer[] nextCells;       // 넥스트 4x4 미리보기
        SpriteRenderer[] holdCells;       // 홀드 4x4 미리보기
        float spriteUnit = 1f;            // 블록 스프라이트 1칸의 월드 크기(스케일 계산용)

        // 입력 반복 타이머
        float leftT, rightT, downT;

        void Awake()
        {
            spriteUnit = blockSprites[0] != null ? blockSprites[0].bounds.size.x : 1f;
            BuildBoard();
            BuildPreview(ref nextCells, new Vector2(TetrisCore.Width + 1.5f, TetrisCore.Height - 5), "Next");
            BuildPreview(ref holdCells, new Vector2(-5.5f, TetrisCore.Height - 5), "Hold");
            if (autoPlay && game == null) game = new TetrisCore(seed);
        }

        // ML/코드에서 코어에 접근하기 위한 프로퍼티.
        public TetrisCore Game => game;

        // 외부(에이전트) 코어를 이 보드가 렌더하도록 연결한다. 이후 이 보드는 입력/중력 없이 렌더만 한다.
        // 예) 에이전트 Initialize() 안에서:  boardRef.Bind(env.Core);
        public void Bind(TetrisCore core)
        {
            game = core;
            autoPlay = false;
        }

        void BuildBoard()
        {
            cells = new SpriteRenderer[TetrisCore.Width, TetrisCore.Height];
            for (int x = 0; x < TetrisCore.Width; x++)
                for (int y = 0; y < TetrisCore.Height; y++)
                    cells[x, y] = MakeCell($"cell_{x}_{y}", CellWorld(x, y), transform);
        }

        void BuildPreview(ref SpriteRenderer[] arr, Vector2 origin, string name)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            root.localPosition = Vector3.zero;
            arr = new SpriteRenderer[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    var pos = new Vector3(origin.x + x * cellSize, origin.y + y * cellSize, 0);
                    arr[y * 4 + x] = MakeCell($"{name}_{x}_{y}", pos, root);
                }
        }

        SpriteRenderer MakeCell(string name, Vector3 pos, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);   // 보드 transform 기준(로컬) 배치 → 보드마다 위치 분리
            go.transform.localPosition = pos;
            float s = cellSize / spriteUnit;
            go.transform.localScale = new Vector3(s, s, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = blockSprites[0];
            sr.enabled = false;
            return sr;
        }

        Vector3 CellWorld(int x, int y) => new Vector3(x * cellSize, y * cellSize, 0);

        void Update()
        {
            if (game == null) return;          // autoPlay=false 이고 아직 Bind 전
            if (autoPlay)
            {
                HandleInput();
                game.Tick(Time.deltaTime);
            }
            Render();
        }

        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.rKey.wasPressedThisFrame) { game.Reset(); return; }
            if (game.GameOver) return;

            // 좌우 이동 (DAS/ARR)
            leftT = Repeat(kb.leftArrowKey, leftT, game.MoveLeft);
            rightT = Repeat(kb.rightArrowKey, rightT, game.MoveRight);

            // 소프트 드롭
            downT = Repeat(kb.downArrowKey, downT, () => game.SoftDrop());

            // 회전: 위/X = CW, Z = CCW
            if (kb.upArrowKey.wasPressedThisFrame || kb.xKey.wasPressedThisFrame) game.Rotate(1);
            if (kb.zKey.wasPressedThisFrame) game.Rotate(-1);

            // 하드 드롭
            if (kb.spaceKey.wasPressedThisFrame) game.HardDrop();

            // 홀드
            if (kb.cKey.wasPressedThisFrame || kb.leftShiftKey.wasPressedThisFrame) game.HoldPiece();
        }

        // 키를 누르는 순간 1회, 이후 dasDelay 뒤 arrRate 간격으로 반복 호출.
        float Repeat(KeyControl key, float timer, System.Func<bool> act)
        {
            if (key.wasPressedThisFrame) { act(); return dasDelay; }
            if (key.isPressed)
            {
                timer -= Time.deltaTime;
                if (timer <= 0f) { act(); return arrRate; }
                return timer;
            }
            return 0f;
        }

        void Render()
        {
            // 보드 초기화
            for (int x = 0; x < TetrisCore.Width; x++)
                for (int y = 0; y < TetrisCore.Height; y++)
                {
                    int v = game.Grid[x, y];
                    var sr = cells[x, y];
                    if (v == TetrisCore.Empty) { sr.enabled = true; sr.sprite = blockSprites[0]; sr.color = emptyColor; }
                    else { sr.enabled = true; sr.sprite = blockSprites[v]; sr.color = Color.white; }
                }

            if (!game.GameOver)
            {
                // 고스트
                foreach (var c in game.GhostCells())
                    if (InBoard(c) && game.Grid[c.x, c.y] == TetrisCore.Empty)
                    { cells[c.x, c.y].sprite = blockSprites[(int)game.Current]; cells[c.x, c.y].color = ghostColor; }
                // 현재 조각
                foreach (var c in game.CurrentCells())
                    if (InBoard(c))
                    { cells[c.x, c.y].sprite = blockSprites[(int)game.Current]; cells[c.x, c.y].color = Color.white; }
            }

            // 넥스트 / 홀드 미리보기
            DrawPreview(nextCells, game.Next.Peek());
            if (game.Hold.HasValue) DrawPreview(holdCells, game.Hold.Value);
            else ClearPreview(holdCells);
        }

        void DrawPreview(SpriteRenderer[] arr, PieceType p)
        {
            ClearPreview(arr);
            foreach (var c in game.CellsAt(p, 0, Vector2Int.zero))
            {
                int idx = c.y * 4 + c.x;
                if (idx >= 0 && idx < 16) { arr[idx].enabled = true; arr[idx].sprite = blockSprites[(int)p]; arr[idx].color = Color.white; }
            }
        }

        void ClearPreview(SpriteRenderer[] arr) { foreach (var sr in arr) sr.enabled = false; }

        bool InBoard(Vector2Int c) => c.x >= 0 && c.x < TetrisCore.Width && c.y >= 0 && c.y < TetrisCore.Height;

        // 보드별 개별 HUD(IMGUI). 각 보드의 월드 위치를 화면 좌표로 변환해 그 보드 옆에 띄운다.
        // → 보드가 여러 개(병렬 에이전트) 떠 있어도 각자 자기 점수를 보여준다.
        // ponytail: 디버그용. 예쁜 UI 필요하면 보드마다 World-Space Canvas + TMP 로 교체.
        GUIStyle hudStyle;

        void OnGUI()
        {
            if (game == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (hudStyle == null)
                hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };

            // 이 보드의 왼쪽 위(Hold 패널 위) 월드 지점 → 화면 좌표
            Vector3 sp = cam.WorldToScreenPoint(transform.TransformPoint(new Vector3(-5.5f, TetrisCore.Height - 0.5f, 0f)));
            if (sp.z < 0f) return;                                    // 카메라 뒤면 그리지 않음
            var r = new Rect(sp.x, Screen.height - sp.y, 220f, 20f);  // GUI는 좌상단 원점

            if (!string.IsNullOrEmpty(statusLabel))
            {
                var prev = GUI.color; GUI.color = Color.cyan;
                GUI.Label(r, statusLabel, hudStyle); r.y += 20f;
                GUI.color = prev;
            }
            GUI.Label(r, name, hudStyle);                       r.y += 20f;
            GUI.Label(r, $"Score {game.Score}", hudStyle);      r.y += 20f;
            GUI.Label(r, $"Level {game.Level}", hudStyle);      r.y += 20f;
            GUI.Label(r, $"Lines {game.Lines}", hudStyle);      r.y += 20f;
            if (game.GameOver) GUI.Label(r, "GAME OVER", hudStyle);

            // 조작 안내는 사람이 직접 플레이하는 보드에만 (보드 아래에 표시)
            if (autoPlay)
            {
                Vector3 cp = cam.WorldToScreenPoint(transform.TransformPoint(new Vector3(0f, -1.2f, 0f)));
                if (cp.z > 0f)
                    GUI.Label(new Rect(cp.x, Screen.height - cp.y, 560f, 40f),
                        "←/→ 이동  ↓ 소프트  Space 하드드롭  ↑/X 회전  Z 반대  C/Shift 홀드  R 리셋", hudStyle);
            }
        }
    }
}
