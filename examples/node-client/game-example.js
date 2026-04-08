/**
 * game-example.js — full Brazilian checkers game simulation using the CheckersEngine HTTP API.
 *
 * Prerequisites:
 *   1. Start the API server:
 *        dotnet run --project examples/CheckersApi
 *      Or with tablebase pre-loaded:
 *        CHECKERS_TABLEBASE_PATH=./tablebase dotnet run --project examples/CheckersApi
 *
 *   2. Run this script (Node 18+):
 *        node game-example.js
 *
 * The script simulates a full game:
 *   - Engine (black) vs simulated human (red — picks random legal moves)
 *   - Shows board state after each move
 *   - Detects draw offers and game over
 *   - Probes the tablebase when positions are covered
 */

'use strict';

const { CheckersClient, CheckersApiError } = require('./checkers-client');

// ─── Configuration ─────────────────────────────────────────────────────────────

const API_URL      = process.env.CHECKERS_API_URL ?? 'http://localhost:5000';
const MAX_MOVES    = 150;   // safety cap to prevent infinite games
const MOVE_DELAY   = 50;    // ms between moves for readability (0 = as fast as possible)
const ENGINE_PRESET = process.env.ENGINE_PRESET ?? 'fast'; // blitz | fast | default | strong

// ─── Helpers ───────────────────────────────────────────────────────────────────

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

/** Pick a random element from an array. */
function randomChoice(arr) { return arr[Math.floor(Math.random() * arr.length)]; }

/** Render the board to a string. */
function renderBoard(board) {
  const rows = [];
  rows.push('  ┌─' + '──┬─'.repeat(7) + '──┐');

  for (let y = 0; y < 8; y++) {
    const cells = board[y].map(cell => {
      switch (cell) {
        case 'b': return ' b';
        case 'B': return ' B';
        case 'r': return ' r';
        case 'R': return ' R';
        default:  return (y + (y % 2 === 0 ? 0 : 1)) % 2 === 0 ? ' ░' : ' ·';
      }
    });
    rows.push(`${y} │${cells.join('│')}│`);
    if (y < 7) rows.push('  ├─' + '──┼─'.repeat(7) + '──┤');
  }

  rows.push('  └─' + '──┴─'.repeat(7) + '──┘');
  rows.push('   ' + Array.from({ length: 8 }, (_, i) => ` ${i} `).join(' '));
  return rows.join('\n');
}

/** Format a move as readable notation. */
function fmtMove(mv) {
  return `(${mv.fx},${mv.fy}) → (${mv.tx},${mv.ty})${mv.isCapture ? ' ✕' : ''}`;
}

/** Print a horizontal separator. */
function sep(char = '─', len = 52) { console.log(char.repeat(len)); }

// ─── Server connectivity check ─────────────────────────────────────────────────

async function waitForServer(client, retries = 10) {
  for (let i = 0; i < retries; i++) {
    try {
      const h = await client.health();
      console.log(`✓ Connected to CheckersApi — tablebase: ${h.tablebase ? `yes (${h.tbConfigs} configs, ${h.tbPositions.toLocaleString()} positions)` : 'no'}`);
      return h;
    } catch {
      if (i === 0) process.stdout.write('  Waiting for API server');
      process.stdout.write('.');
      await sleep(1000);
    }
  }
  console.error('\n✗ Could not reach API. Is "dotnet run --project examples/CheckersApi" running?');
  process.exit(1);
}

// ─── Main game loop ─────────────────────────────────────────────────────────────

async function playGame(client) {
  // 1. Start a game
  const { gameId, tablebase: tbLoaded } = await client.startGame({
    preset:        ENGINE_PRESET,
    useOpeningBook: true,
  });

  console.log(`\n  Game ID  : ${gameId}`);
  console.log(`  Preset   : ${ENGINE_PRESET}`);
  console.log(`  Tablebase: ${tbLoaded ? 'loaded' : 'not loaded'}`);
  sep();

  let moveCount   = 0;
  let blackTurn   = true;   // engine plays black, moves first
  let gameOver    = false;
  let winner      = null;

  while (!gameOver && moveCount < MAX_MOVES) {
    // Show current board
    const state = await client.getGame(gameId);
    console.log(`\n  Move #${moveCount + 1}  [${state.turn.toUpperCase()} to move]`);
    console.log(renderBoard(state.board));

    // Tablebase probe
    const probe = await client.probe(gameId);
    if (probe.covered) {
      console.log(`  ♟ Tablebase: ${probe.label}  (side to move)`);
    }

    // ── Engine turn (black) ──────────────────────────────────────────────────
    if (blackTurn) {
      const result = await client.engineMove(gameId);

      if (result.gameOver && !result.move) {
        console.log('\n  ✗ Engine has no moves — RED wins!');
        winner   = 'red';
        gameOver = true;
        break;
      }

      console.log(`  ♟ Engine plays: ${fmtMove(result.move)}`);

      if (result.tablebase) {
        console.log(`     Tablebase after move: ${result.tablebase.label} for red`);
      }

      if (result.drawOffer) {
        console.log(`\n  ═══ Engine offers draw: "${result.drawOffer}" ═══`);
        // In a real game the human would decide; here we auto-decline
        console.log('  (Simulated human declines the draw offer)');
      }

      gameOver  = result.gameOver;
      winner    = result.winner;
      blackTurn = false;

    // ── Human turn (red) ─────────────────────────────────────────────────────
    } else {
      const { moves } = await client.legalMoves(gameId, false /* red */);

      if (moves.length === 0) {
        console.log('\n  ✗ Human has no moves — BLACK (engine) wins!');
        winner   = 'black';
        gameOver = true;
        break;
      }

      // Simulate a human by picking a random legal move
      // Prefer captures if available (realistic player behavior)
      const captures = moves.filter(m => m.isCapture);
      const chosen   = captures.length > 0 ? randomChoice(captures) : randomChoice(moves);

      console.log(`  🔴 Human plays: ${fmtMove(chosen)}`);

      // Occasionally simulate a draw offer (1-in-15 chance after move 20)
      if (moveCount > 20 && Math.random() < 1 / 15) {
        const draw = await client.offerDraw(gameId);
        if (draw.accepted) {
          console.log(`\n  ═══ Draw agreed: "${draw.reason}" ═══`);
          gameOver = true;
          winner   = null;
          break;
        } else {
          console.log(`  (Human offers draw — engine refuses: "${draw.reason}")`);
        }
      }

      const result = await client.humanMove(gameId, chosen);
      gameOver  = result.gameOver;
      winner    = result.winner;
      blackTurn = true;
    }

    moveCount++;
    if (MOVE_DELAY > 0) await sleep(MOVE_DELAY);
  }

  // ─── Final board ────────────────────────────────────────────────────────────

  sep('═');
  const final = await client.getGame(gameId);
  console.log('\n  Final board:');
  console.log(renderBoard(final.board));
  sep();

  if (moveCount >= MAX_MOVES) {
    console.log(`\n  ⏹ Move limit (${MAX_MOVES}) reached — result: Draw`);
  } else if (winner === 'black') {
    console.log('\n  🏆 BLACK (engine) wins!');
  } else if (winner === 'red') {
    console.log('\n  🏆 RED (human) wins!');
  } else {
    console.log('\n  🤝 Draw agreed.');
  }

  console.log(`  Total moves played: ${moveCount}`);

  // ─── Human profile ──────────────────────────────────────────────────────────
  const profile = await client.humanProfile(gameId);
  console.log('\n  Human player profile:');
  console.log(`    Aggression rate : ${(profile.aggressionRate * 100).toFixed(1)} % (captures)`);
  console.log(`    Left flank rate : ${(profile.leftFlankRate  * 100).toFixed(1)} %`);
  console.log(`    Avg advance     : ${profile.avgAdvance.toFixed(2)} rows/move`);
  console.log(`    Moves recorded  : ${profile.gamesLearned}`);

  // ─── Cleanup ─────────────────────────────────────────────────────────────────
  await client.deleteGame(gameId);
  console.log(`\n  Game ${gameId} deleted.`);
  sep('─');
}

// ─── Entry point ────────────────────────────────────────────────────────────────

(async () => {
  const client = new CheckersClient({ baseUrl: API_URL });

  sep('═');
  console.log('  CheckersEngine — Node.js Game Example');
  console.log(`  API: ${API_URL}`);
  sep('═');

  const health = await waitForServer(client);

  console.log('\nStarting game simulation …');
  await playGame(client);

  console.log('\nDone.');
})();
