using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tetris
{
    // 조각 종류. 렌더러의 blockSprites 인덱스와 그리드 셀 값이 이 순서(int)를 따른다.
    public enum PieceType { I = 0, O = 1, T = 2, S = 3, Z = 4, J = 5, L = 6 }

    /// <summary>
    /// Unity(MonoBehaviour) 비의존 순수 테트리스 게임 로직.
    /// ML-Agents 에이전트가 이 클래스를 직접 만들어 조작(Move/Rotate/Drop/Hold/Tick)하고
    /// Grid / CurrentCells / Score 등을 관측(observation)으로 읽어 학습에 사용할 수 있다.
    /// 표준(가이드라인) 규칙: 10x20, 7-bag, SRS 회전+월킥, 홀드, 넥스트, 라인클리어 점수/레벨.
    /// </summary>
    public class TetrisCore
    {
        public const int Width = 10;
        public const int Height = 20;
        public const int Empty = -1;

        // Grid[x, y] : Empty(-1) 또는 (int)PieceType. y=0 바닥, y=Height-1 최상단.
        public readonly int[,] Grid = new int[Width, Height];

        public PieceType Current;
        public int Rotation;              // 0,R(1),2,L(3)
        public Vector2Int Pos;            // 조각 바운딩박스 (0,0) 셀의 보드 좌표
        public PieceType? Hold;
        public bool HoldUsed;             // 조각당 홀드 1회 제한
        public readonly Queue<PieceType> Next = new Queue<PieceType>();

        public int Score, Lines, Level;
        public bool GameOver;

        // 최근 락 결과(렌더러 SFX 훅용). LinesClearedLast: 방금 지운 줄 수.
        public int LinesClearedLast;
        public bool JustLocked;

        // 타이밍
        public float GravityTimer;
        public float LockTimer;
        public bool Landed;               // 조각이 스택 위에 얹혀 락 대기 중
        int lockResets;
        const int MaxLockResets = 15;
        const float LockDelay = 0.5f;
        const int PreviewCount = 5;       // 넥스트 큐 유지 개수

        static readonly int[] LineScore = { 0, 100, 300, 500, 800 }; // single/double/triple/tetris
        readonly System.Random rng;
        readonly List<PieceType> bag = new List<PieceType>();

        // [piece][rotation] -> 4개 셀 오프셋(바운딩박스 내부, y-up)
        static Vector2Int[][][] shapes;
        // 스폰 시 바운딩박스 (0,0)의 보드 좌표
        static Vector2Int[] spawnPos;
        // SRS 월킥 테이블
        static Dictionary<int, Vector2Int[]> kicksJLSTZ, kicksI;

        static TetrisCore() { BuildShapes(); BuildKicks(); }

        public TetrisCore(int seed = 0)
        {
            rng = seed == 0 ? new System.Random() : new System.Random(seed);
            Reset();
        }

        public void Reset()
        {
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    Grid[x, y] = Empty;
            Score = 0; Lines = 0; Level = 1; GameOver = false;
            Hold = null; HoldUsed = false; Landed = false;
            GravityTimer = LockTimer = 0; lockResets = 0;
            LinesClearedLast = 0; JustLocked = false;
            Next.Clear(); bag.Clear();
            for (int i = 0; i < PreviewCount; i++) Next.Enqueue(NextFromBag());
            SpawnNext();
        }

        // ---- 7-bag 랜덤 ----
        PieceType NextFromBag()
        {
            if (bag.Count == 0)
            {
                for (int i = 0; i < 7; i++) bag.Add((PieceType)i);
                for (int i = bag.Count - 1; i > 0; i--) // Fisher-Yates
                {
                    int j = rng.Next(i + 1);
                    (bag[i], bag[j]) = (bag[j], bag[i]);
                }
            }
            var p = bag[bag.Count - 1];
            bag.RemoveAt(bag.Count - 1);
            return p;
        }

        // ---- 조각/충돌 ----
        public Vector2Int[] CellsAt(PieceType p, int rot, Vector2Int pos)
        {
            var s = shapes[(int)p][((rot % 4) + 4) % 4];
            var r = new Vector2Int[4];
            for (int i = 0; i < 4; i++) r[i] = s[i] + pos;
            return r;
        }

        public Vector2Int[] CurrentCells() => CellsAt(Current, Rotation, Pos);

        bool Fits(PieceType p, int rot, Vector2Int pos)
        {
            foreach (var c in CellsAt(p, rot, pos))
            {
                if (c.x < 0 || c.x >= Width || c.y < 0) return false;
                if (c.y < Height && Grid[c.x, c.y] != Empty) return false; // y>=Height는 필드 위 빈 공간
            }
            return true;
        }

        void SpawnNext()
        {
            Current = Next.Dequeue();
            Next.Enqueue(NextFromBag());
            Rotation = 0;
            Pos = spawnPos[(int)Current];
            HoldUsed = false; Landed = false; LockTimer = 0; lockResets = 0;
            if (!Fits(Current, Rotation, Pos)) GameOver = true; // 탑아웃
        }

        // ---- 플레이어/에이전트 액션 ----
        public bool MoveLeft() => TryMove(Vector2Int.left);
        public bool MoveRight() => TryMove(Vector2Int.right);

        bool TryMove(Vector2Int d)
        {
            if (GameOver) return false;
            if (Fits(Current, Rotation, Pos + d)) { Pos += d; OnMoved(); return true; }
            return false;
        }

        // 소프트 드롭: 한 칸 하강 성공 시 +1점.
        public bool SoftDrop()
        {
            if (GameOver) return false;
            if (Fits(Current, Rotation, Pos + Vector2Int.down))
            {
                Pos += Vector2Int.down; Score += 1; GravityTimer = 0;
                Landed = false; LockTimer = 0;
                return true;
            }
            Landed = true;
            return false;
        }

        // 하드 드롭: 바닥까지 내려 즉시 락. 내려간 칸당 +2점.
        public void HardDrop()
        {
            if (GameOver) return;
            int dist = 0;
            while (Fits(Current, Rotation, Pos + Vector2Int.down)) { Pos += Vector2Int.down; dist++; }
            Score += dist * 2;
            LockPiece();
        }
        // ---- 에이전트용 그리드 배치 액션 ----
        // 현재 조각을 목표 column(가장 왼쪽 셀의 x)과 rotation(0..3)으로 상단에서 정렬한 뒤
        // 즉시 하드드롭해 그 위치에 배치한다. 배치 불가(범위 밖/막힘)면 상태를 바꾸지 않고 false.
        // ML 에이전트의 고수준 이산 액션(열 x 회전)에 대응한다.
        public bool PlacePiece(int column, int rotation)
        {
            if (GameOver) return false;
            if (!ResolvePlacement(column, rotation, out int rot, out Vector2Int pos)) return false;
            Rotation = rot;
            Pos = pos;
            HardDrop();
            return true;
        }

        // 배치 가능 여부만 검사(상태 불변). 에이전트 액션 마스킹용.
        public bool CanPlace(int column, int rotation)
        {
            if (GameOver) return false;
            return ResolvePlacement(column, rotation, out _, out _);
        }

        // column/rotation -> 상단 정렬 위치 계산 + 유효성 검사.
        bool ResolvePlacement(int column, int rotation, out int rot, out Vector2Int pos)
        {
            rot = ((rotation % 4) + 4) % 4;
            var cells = shapes[(int)Current][rot];
            int minX = int.MaxValue;
            foreach (var c in cells) if (c.x < minX) minX = c.x;
            pos = new Vector2Int(column - minX, spawnPos[(int)Current].y);
            return Fits(Current, rot, pos);
        }


        // ---- 관전용 단계적 배치 (애니메이션) ----
        // PlacePiece 는 즉시 하드드롭하지만, 이쪽은 목표(column,rotation)까지
        // 회전 → 수평이동 → 낙하를 한 스텝씩 진행한다. 렌더러가 매 틱 StepPlacement()를
        // 호출하면 조각이 움직이는 과정이 그대로 그려진다. 최종 결과는 PlacePiece 와 동일.
        int placeTargetX, placeTargetRot;
        bool placing;

        // 목표 위치로 단계 배치 시작. 배치 불가면 false(상태 불변).
        public bool BeginPlacement(int column, int rotation)
        {
            if (GameOver) return false;
            if (!ResolvePlacement(column, rotation, out int rot, out Vector2Int pos)) return false;
            placeTargetRot = Current == PieceType.O ? Rotation : rot; // O는 회전 무의미 → 스킵
            placeTargetX = pos.x;
            placing = true;
            return true;
        }

        // 한 스텝 진행. 락(고정) 완료 시 true. 종료 보장(무한루프 없음) — 막히면 목표로 스냅.
        public bool StepPlacement()
        {
            if (!placing) return true;

            if (Rotation != placeTargetRot)                  // 1) 회전 정렬
            {
                int before = Rotation;
                Rotate(1);
                if (Rotation == before)                      // O/막힘 → 목표로 스냅
                    { Rotation = placeTargetRot; Pos = new Vector2Int(placeTargetX, spawnPos[(int)Current].y); }
                return false;
            }
            if (Pos.x != placeTargetX)                       // 2) 수평 이동 정렬
            {
                int before = Pos.x;
                if (Pos.x < placeTargetX) MoveRight(); else MoveLeft();
                if (Pos.x != before) return false;
                Pos = new Vector2Int(placeTargetX, spawnPos[(int)Current].y); // 막힘 → 스냅
            }
            if (SoftDrop()) return false;                    // 3) 낙하
            HardDrop();                                      // 바닥 → 락
            placing = false;
            return true;
        }

        public bool Rotate(int dir) // +1 CW, -1 CCW
        {
            if (GameOver) return false;
            if (Current == PieceType.O) return true; // O는 회전 불필요
            int from = Rotation;
            int to = ((from + dir) % 4 + 4) % 4;
            var table = Current == PieceType.I ? kicksI : kicksJLSTZ;
            foreach (var k in table[from * 4 + to])
            {
                if (Fits(Current, to, Pos + k)) { Pos += k; Rotation = to; OnMoved(); return true; }
            }
            return false;
        }

        public void HoldPiece()
        {
            if (GameOver || HoldUsed) return;
            if (Hold == null)
            {
                Hold = Current;
                SpawnNext();
            }
            else
            {
                var swap = Hold.Value;
                Hold = Current;
                Current = swap;
                Rotation = 0;
                Pos = spawnPos[(int)Current];
                Landed = false; LockTimer = 0; lockResets = 0;
                if (!Fits(Current, Rotation, Pos)) GameOver = true;
            }
            HoldUsed = true;
        }

        // 이동/회전 성공 시 락딜레이 리셋(최대 횟수 제한).
        void OnMoved()
        {
            if (Landed && lockResets < MaxLockResets)
            {
                LockTimer = 0; lockResets++;
                if (!Fits(Current, Rotation, Pos + Vector2Int.down)) Landed = true;
                else Landed = false;
            }
        }

        // ---- 시간 진행(중력+락딜레이). dt 초. ----
        public void Tick(float dt)
        {
            if (GameOver) return;
            JustLocked = false; LinesClearedLast = 0;

            GravityTimer += dt;
            float interval = GravityInterval();
            while (GravityTimer >= interval)
            {
                GravityTimer -= interval;
                if (Fits(Current, Rotation, Pos + Vector2Int.down))
                {
                    Pos += Vector2Int.down; Landed = false; LockTimer = 0;
                }
                else { Landed = true; break; }
            }

            if (Landed)
            {
                LockTimer += dt;
                if (LockTimer >= LockDelay) LockPiece();
            }
        }

        public float GravityInterval()
        {
            // 가이드라인 중력 곡선: (0.8 - (level-1)*0.007)^(level-1) 초/칸.
            double basev = 0.8 - (Level - 1) * 0.007;
            if (basev < 0.02) basev = 0.02;
            return (float)Math.Pow(basev, Level - 1);
        }

        // ---- 락 + 라인클리어 + 점수 ----
        void LockPiece()
        {
            bool topOut = false;
            foreach (var c in CurrentCells())
            {
                if (c.y >= Height) { topOut = true; continue; }
                Grid[c.x, c.y] = (int)Current;
            }
            int cleared = ClearFullLines();
            LinesClearedLast = cleared;
            JustLocked = true;

            if (cleared > 0)
            {
                Score += LineScore[cleared] * Level;
                Lines += cleared;
                Level = 1 + Lines / 10;
            }

            Landed = false; LockTimer = 0; lockResets = 0;
            if (topOut) { GameOver = true; return; }
            SpawnNext();
        }

        // 꽉 찬 줄 제거 후 위 줄을 아래로 내림. 제거한 줄 수 반환.
        public int ClearFullLines()
        {
            int write = 0;
            for (int y = 0; y < Height; y++)
            {
                bool full = true;
                for (int x = 0; x < Width; x++) if (Grid[x, y] == Empty) { full = false; break; }
                if (full) continue;
                if (write != y)
                    for (int x = 0; x < Width; x++) Grid[x, write] = Grid[x, y];
                write++;
            }
            int cleared = Height - write;
            for (int y = write; y < Height; y++)
                for (int x = 0; x < Width; x++) Grid[x, y] = Empty;
            return cleared;
        }

        // 현재 조각이 곧장 하드드롭될 착지 위치의 셀들(고스트 표시용).
        public Vector2Int[] GhostCells()
        {
            var pos = Pos;
            while (Fits(Current, Rotation, pos + Vector2Int.down)) pos += Vector2Int.down;
            return CellsAt(Current, Rotation, pos);
        }

        // ================= 정적 데이터 빌드 =================
        static void BuildShapes()
        {
            shapes = new Vector2Int[7][][];
            spawnPos = new Vector2Int[7];

            // 3x3 박스, y-up (행0=하단). 중심 (1,1)로 회전.
            var t = new[] { V(0, 1), V(1, 1), V(2, 1), V(1, 2) };
            var j = new[] { V(0, 2), V(0, 1), V(1, 1), V(2, 1) };
            var l = new[] { V(2, 2), V(0, 1), V(1, 1), V(2, 1) };
            var s = new[] { V(1, 2), V(2, 2), V(0, 1), V(1, 1) };
            var z = new[] { V(0, 2), V(1, 2), V(1, 1), V(2, 1) };
            shapes[(int)PieceType.T] = Rotations(t, new Vector2(1, 1), true);
            shapes[(int)PieceType.J] = Rotations(j, new Vector2(1, 1), true);
            shapes[(int)PieceType.L] = Rotations(l, new Vector2(1, 1), true);
            shapes[(int)PieceType.S] = Rotations(s, new Vector2(1, 1), true);
            shapes[(int)PieceType.Z] = Rotations(z, new Vector2(1, 1), true);

            // I: 4x4 박스, 중심 (1.5,1.5).
            var iSpawn = new[] { V(0, 2), V(1, 2), V(2, 2), V(3, 2) };
            shapes[(int)PieceType.I] = Rotations(iSpawn, new Vector2(1.5f, 1.5f), true);

            // O: 2x2, 회전 없음.
            var oSpawn = new[] { V(0, 0), V(1, 0), V(0, 1), V(1, 1) };
            shapes[(int)PieceType.O] = Rotations(oSpawn, new Vector2(0.5f, 0.5f), false);

            // 스폰 위치: 박스를 상단 중앙에 배치.
            spawnPos[(int)PieceType.I] = V(3, 16); // cells at y=18, cols 3..6
            spawnPos[(int)PieceType.O] = V(4, 18); // cols 4,5 ; y 18,19
            spawnPos[(int)PieceType.T] = V(3, 17);
            spawnPos[(int)PieceType.J] = V(3, 17);
            spawnPos[(int)PieceType.L] = V(3, 17);
            spawnPos[(int)PieceType.S] = V(3, 17);
            spawnPos[(int)PieceType.Z] = V(3, 17);
        }

        static Vector2Int[][] Rotations(Vector2Int[] spawn, Vector2 center, bool rotate)
        {
            var states = new Vector2Int[4][];
            states[0] = spawn;
            for (int r = 1; r < 4; r++)
            {
                if (!rotate) { states[r] = spawn; continue; }
                var prev = states[r - 1];
                var cur = new Vector2Int[4];
                for (int i = 0; i < 4; i++)
                {
                    float rx = prev[i].x - center.x;
                    float ry = prev[i].y - center.y;
                    // CW 90도: (x,y) -> (y, -x)
                    cur[i] = new Vector2Int(
                        Mathf.RoundToInt(ry + center.x),
                        Mathf.RoundToInt(-rx + center.y));
                }
                states[r] = cur;
            }
            return states;
        }

        static void BuildKicks()
        {
            kicksJLSTZ = new Dictionary<int, Vector2Int[]>
            {
                { K(0,1), new[]{ V(0,0),V(-1,0),V(-1,1),V(0,-2),V(-1,-2) } },
                { K(1,0), new[]{ V(0,0),V(1,0),V(1,-1),V(0,2),V(1,2) } },
                { K(1,2), new[]{ V(0,0),V(1,0),V(1,-1),V(0,2),V(1,2) } },
                { K(2,1), new[]{ V(0,0),V(-1,0),V(-1,1),V(0,-2),V(-1,-2) } },
                { K(2,3), new[]{ V(0,0),V(1,0),V(1,1),V(0,-2),V(1,-2) } },
                { K(3,2), new[]{ V(0,0),V(-1,0),V(-1,-1),V(0,2),V(-1,2) } },
                { K(3,0), new[]{ V(0,0),V(-1,0),V(-1,-1),V(0,2),V(-1,2) } },
                { K(0,3), new[]{ V(0,0),V(1,0),V(1,1),V(0,-2),V(1,-2) } },
            };
            kicksI = new Dictionary<int, Vector2Int[]>
            {
                { K(0,1), new[]{ V(0,0),V(-2,0),V(1,0),V(-2,-1),V(1,2) } },
                { K(1,0), new[]{ V(0,0),V(2,0),V(-1,0),V(2,1),V(-1,-2) } },
                { K(1,2), new[]{ V(0,0),V(-1,0),V(2,0),V(-1,2),V(2,-1) } },
                { K(2,1), new[]{ V(0,0),V(1,0),V(-2,0),V(1,-2),V(-2,1) } },
                { K(2,3), new[]{ V(0,0),V(2,0),V(-1,0),V(2,1),V(-1,-2) } },
                { K(3,2), new[]{ V(0,0),V(-2,0),V(1,0),V(-2,-1),V(1,2) } },
                { K(3,0), new[]{ V(0,0),V(1,0),V(-2,0),V(1,-2),V(-2,1) } },
                { K(0,3), new[]{ V(0,0),V(-1,0),V(2,0),V(-1,2),V(2,-1) } },
            };
        }

        static Vector2Int V(int x, int y) => new Vector2Int(x, y);
        static int K(int from, int to) => from * 4 + to;

        // ================= 셀프테스트 =================
        // execute_code 로 TetrisCore.SelfTest() 호출해 검증.
        public static string SelfTest()
        {
            var g = new TetrisCore(seed: 12345);
            A(!g.GameOver && g.Score == 0 && g.Level == 1, "초기 상태");
            A(g.CurrentCells().Length == 4, "조각 4셀");
            A(g.Next.Count == PreviewCount, "넥스트 큐 개수");

            // 회전 4회 원위치 (T)
            var start = g.CurrentCells();
            g.Current = PieceType.T; g.Rotation = 0; g.Pos = spawnPos[(int)PieceType.T];
            var t0 = g.CurrentCells();
            for (int i = 0; i < 4; i++) g.Rotate(1);
            var t4 = g.CurrentCells();
            for (int i = 0; i < 4; i++) A(t0[i] == t4[i], "회전 4회 원위치");

            // 라인클리어 + 점수
            var g2 = new TetrisCore(seed: 1);
            for (int x = 0; x < Width; x++) g2.Grid[x, 0] = (int)PieceType.O;
            g2.Grid[3, 1] = (int)PieceType.O; // 위 줄에 블록 하나 → 클리어 후 y=0으로 내려와야 함
            int before = g2.Level;
            int cleared = g2.ClearFullLines();
            A(cleared == 1, "한 줄 클리어");
            A(g2.Grid[3, 0] == (int)PieceType.O, "위 줄 하강");
            for (int x = 0; x < Width; x++) if (x != 3) A(g2.Grid[x, 0] == Empty, "클리어 후 빈칸");

            // 하드드롭: 빈 보드에 하드드롭하면 4셀이 그리드에 고정되고 점수 증가
            var g3 = new TetrisCore(seed: 7);
            int occ0 = CountOccupied(g3);
            int sc0 = g3.Score;
            g3.HardDrop();
            int occ1 = CountOccupied(g3);
            A(occ1 == occ0 + 4, "하드드롭 4셀 고정");
            A(g3.Score > sc0, "하드드롭 점수 증가");

            // 점수 배율: 테트리스(4줄) = 800 * level
            var g4 = new TetrisCore(seed: 2);
            g4.Level = 3;
            for (int y = 0; y < 4; y++) for (int x = 0; x < Width; x++) g4.Grid[x, y] = (int)PieceType.I;
            int scoreBefore = g4.Score;
            int c4 = g4.ClearFullLines();
            A(c4 == 4, "4줄 감지");
            // ClearFullLines는 점수를 더하지 않음(LockPiece에서 더함). 점수 규칙만 직접 확인.
            A(LineScore[4] == 800, "테트리스 기본점 800");

            // 그리드 배치 액션: 빈 보드에 배치하면 그리드에 4셀 고정
            var g5 = new TetrisCore(seed: 3);
            A(g5.PlacePiece(0, 0), "PlacePiece 유효 배치");
            A(CountOccupied(g5) == 4, "배치 후 4셀 고정");

            // 범위 밖 배치는 거부되고 상태 불변
            var g6 = new TetrisCore(seed: 5);
            g6.Current = PieceType.I; g6.Rotation = 0; g6.Pos = spawnPos[(int)PieceType.I];
            A(!g6.PlacePiece(9, 0), "범위 밖 배치 거부");
            A(CountOccupied(g6) == 0, "거부 시 상태 불변");
            A(g6.CanPlace(0, 0) && !g6.CanPlace(9, 0), "CanPlace 유효성");

            // 단계적 배치(애니메이션)는 즉시 배치와 같은 최종 그리드를 만들고, 반드시 종료한다.
            var gInstant = new TetrisCore(seed: 42);
            var gStep = new TetrisCore(seed: 42);
            A(gInstant.PlacePiece(0, 0), "즉시 배치 유효");
            A(gStep.BeginPlacement(0, 0), "단계 배치 시작");
            int steps = 0;
            while (!gStep.StepPlacement()) if (++steps > 300) throw new Exception("SelfTest FAILED: StepPlacement 무한루프");
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    A(gStep.Grid[x, y] == gInstant.Grid[x, y], "단계 배치 == 즉시 배치 그리드");

            return "SelfTest OK";
        }

        static int CountOccupied(TetrisCore g)
        {
            int n = 0;
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (g.Grid[x, y] != Empty) n++;
            return n;
        }

        static void A(bool cond, string msg)
        {
            if (!cond) throw new Exception("SelfTest FAILED: " + msg);
        }
    }
}
