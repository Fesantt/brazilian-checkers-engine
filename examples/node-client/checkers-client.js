/**
 * CheckersClient — thin wrapper around the CheckersEngine HTTP API.
 *
 * Requires Node.js 18+ (uses built-in fetch).
 * Start the API server first:
 *   dotnet run --project examples/CheckersApi
 *
 * @example
 * const client = new CheckersClient();
 * const game   = await client.startGame({ preset: 'default' });
 * const result = await client.engineMove(game.gameId);
 * console.log(result.move); // { fx, fy, tx, ty, isCapture }
 */

'use strict';

class CheckersClient {
  /**
   * @param {object} [options]
   * @param {string} [options.baseUrl='http://localhost:5000'] - API base URL.
   * @param {number} [options.timeoutMs=30_000] - Request timeout in milliseconds.
   */
  constructor({ baseUrl = 'http://localhost:5000', timeoutMs = 30_000 } = {}) {
    this.baseUrl   = baseUrl.replace(/\/$/, '');
    this.timeoutMs = timeoutMs;
  }

  // ─── Internal ───────────────────────────────────────────────────────────────

  async #request(method, path, body) {
    const controller = new AbortController();
    const timer      = setTimeout(() => controller.abort(), this.timeoutMs);

    try {
      const res = await fetch(`${this.baseUrl}${path}`, {
        method,
        headers: body != null ? { 'Content-Type': 'application/json' } : {},
        body:    body != null ? JSON.stringify(body) : undefined,
        signal:  controller.signal,
      });

      const text = await res.text();
      let   data;
      try { data = JSON.parse(text); } catch { data = text; }

      if (!res.ok) {
        const msg = typeof data === 'object' ? (data.error ?? JSON.stringify(data)) : text;
        throw new CheckersApiError(msg, res.status, data);
      }

      return data;
    } finally {
      clearTimeout(timer);
    }
  }

  // ─── Server ─────────────────────────────────────────────────────────────────

  /**
   * Check if the API server is running.
   * @returns {Promise<{ status: string, tablebase: boolean, tbConfigs: number, tbPositions: number }>}
   */
  health() { return this.#request('GET', '/health'); }

  /**
   * Generate the endgame tablebase at runtime (blocking — may take minutes for 6 pieces).
   * @param {object} [options]
   * @param {number} [options.maxPieces=6]
   * @param {string} [options.outputPath='./tablebase']
   * @returns {Promise<{ maxPieces, configs, totalPositions, diskBytes, configLog }>}
   */
  generateTablebase({ maxPieces = 6, outputPath = './tablebase' } = {}) {
    return this.#request('POST', '/tablebase/generate', { maxPieces, outputPath });
  }

  // ─── Game lifecycle ──────────────────────────────────────────────────────────

  /**
   * Start a new game.
   * @param {object} [config]
   * @param {'blitz'|'fast'|'default'|'strong'} [config.preset='default'] - Base configuration preset.
   * @param {number}  [config.thinkingMs]     - Override think time (ms).
   * @param {number}  [config.minDepth]        - Override minimum depth.
   * @param {boolean} [config.useOpeningBook]  - Toggle opening book.
   * @param {boolean} [config.useLMR]          - Toggle Late Move Reductions.
   * @param {boolean} [config.useQuiescence]   - Toggle quiescence search.
   * @param {string}  [config.tablebasePath]   - Load tablebase from this directory.
   * @returns {Promise<{ gameId: string, board: string[][], turn: string, tablebase: boolean }>}
   */
  startGame(config = {}) {
    return this.#request('POST', '/game/start', config);
  }

  /**
   * Get the current game state.
   * @param {string} gameId
   * @returns {Promise<{ board, turn, gameOver, winner }>}
   */
  getGame(gameId) { return this.#request('GET', `/game/${gameId}`); }

  /**
   * Delete a game session and free server memory.
   * @param {string} gameId
   */
  deleteGame(gameId) { return this.#request('DELETE', `/game/${gameId}`); }

  /**
   * Reset the board to the starting position (preserves cross-game memory).
   * @param {string} gameId
   */
  resetGame(gameId) { return this.#request('POST', `/game/${gameId}/reset`); }

  // ─── Moves ──────────────────────────────────────────────────────────────────

  /**
   * Let the engine (black) make its move.
   * @param {string} gameId
   * @returns {Promise<{
   *   move: { fx, fy, tx, ty, isCapture },
   *   board: string[][],
   *   turn: string,
   *   gameOver: boolean,
   *   winner: string|null,
   *   drawOffer: string|null,
   *   tablebase: { outcome, dtm, label }|null
   * }>}
   */
  engineMove(gameId) {
    return this.#request('POST', `/game/${gameId}/engine-move`);
  }

  /**
   * Apply the human's (red) move.
   * @param {string} gameId
   * @param {{ fx: number, fy: number, tx: number, ty: number }} move
   * @returns {Promise<{ board, turn, gameOver, winner }>}
   */
  humanMove(gameId, move) {
    return this.#request('POST', `/game/${gameId}/human-move`, move);
  }

  /**
   * Get all legal moves for a given side.
   * @param {string}  gameId
   * @param {boolean} [black] - Defaults to the current turn.
   * @returns {Promise<{ side: string, moves: Array<{ fx, fy, tx, ty, isCapture }> }>}
   */
  legalMoves(gameId, black) {
    const q = black != null ? `?black=${black}` : '';
    return this.#request('GET', `/game/${gameId}/legal-moves${q}`);
  }

  // ─── Draw ───────────────────────────────────────────────────────────────────

  /**
   * Human offers a draw — returns whether the engine accepts.
   * @param {string} gameId
   * @returns {Promise<{ accepted: boolean, reason: string, gameOver: boolean }>}
   */
  offerDraw(gameId) { return this.#request('POST', `/game/${gameId}/draw/offer`); }

  /**
   * Check if the engine wants to proactively offer a draw.
   * @param {string} gameId
   * @returns {Promise<{ shouldOffer: boolean, message: string|null }>}
   */
  shouldOfferDraw(gameId) { return this.#request('GET', `/game/${gameId}/draw/should-offer`); }

  // ─── Analysis ───────────────────────────────────────────────────────────────

  /**
   * Probe the endgame tablebase for the current position.
   * @param {string} gameId
   * @returns {Promise<{ covered: boolean, outcome: string|null, dtm: number|null, label: string|null }>}
   */
  probe(gameId) { return this.#request('GET', `/game/${gameId}/probe`); }

  /**
   * Get the human player's behavioral profile (cross-game statistics).
   * @param {string} gameId
   * @returns {Promise<{ aggressionRate, leftFlankRate, rightFlankRate, avgAdvance, gamesLearned }>}
   */
  humanProfile(gameId) { return this.#request('GET', `/game/${gameId}/profile`); }
}

class CheckersApiError extends Error {
  constructor(message, statusCode, body) {
    super(message);
    this.name       = 'CheckersApiError';
    this.statusCode = statusCode;
    this.body       = body;
  }
}

module.exports = { CheckersClient, CheckersApiError };
