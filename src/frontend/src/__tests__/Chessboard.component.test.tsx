import { afterEach, describe, expect, test } from 'bun:test'
import { cleanup, fireEvent, render } from '@testing-library/react'
import { Chessboard } from '../components/Chessboard'

const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1'
const AFTER_E4_FEN = 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1'
// White pawn on a7 ready to promote; kings far apart
const PROMOTION_FEN = '4k3/P7/8/8/8/8/8/4K3 w - - 0 1'

const square = (container: HTMLElement, name: string) => {
  const el = container.querySelector(`[data-square="${name}"]`)
  if (!el) throw new Error(`square ${name} not rendered`)
  return el
}

afterEach(cleanup)

describe('Chessboard component', () => {
  test('renders 64 squares and 32 pieces for the starting position', () => {
    const { container } = render(<Chessboard fen={START_FEN} />)
    expect(container.querySelectorAll('[data-square]').length).toBe(64)
    expect(container.querySelectorAll('[data-square] svg').length).toBe(32)
  })

  test('selecting a piece highlights its legal target squares', () => {
    const { container } = render(<Chessboard fen={START_FEN} />)
    fireEvent.click(square(container, 'e2'))
    const targets = [...container.querySelectorAll('[data-legal-target]')].map((el) =>
      el.getAttribute('data-square')
    )
    expect(targets.sort()).toEqual(['e3', 'e4'])
  })

  test('click-to-move executes the move and calls onMove', () => {
    let received: { fen: string; from: string; to: string; sanitized: string } | null = null
    const { container } = render(
      <Chessboard
        fen={START_FEN}
        onMove={(fen, details) => {
          received = { fen, ...details }
        }}
      />
    )
    fireEvent.click(square(container, 'e2'))
    fireEvent.click(square(container, 'e4'))

    expect(received).not.toBeNull()
    expect(received!.from).toBe('e2')
    expect(received!.to).toBe('e4')
    expect(received!.sanitized).toBe('e4')
    expect(received!.fen).toContain('4P3')
    // The pawn moved on the board
    expect(square(container, 'e4').querySelector('svg')).not.toBeNull()
    expect(square(container, 'e2').querySelector('svg')).toBeNull()
  })

  test('does not move when disabled', () => {
    let moved = false
    const { container } = render(
      <Chessboard fen={START_FEN} onMove={() => { moved = true }} disabled />
    )
    fireEvent.click(square(container, 'e2'))
    fireEvent.click(square(container, 'e4'))
    expect(moved).toBe(false)
  })

  test('syncs the board when the fen prop changes externally (opponent move via SignalR)', () => {
    const { container, rerender } = render(<Chessboard fen={START_FEN} />)
    expect(square(container, 'e4').querySelector('svg')).toBeNull()

    rerender(<Chessboard fen={AFTER_E4_FEN} />)

    expect(square(container, 'e4').querySelector('svg')).not.toBeNull()
    expect(square(container, 'e2').querySelector('svg')).toBeNull()
  })

  test('pawn promotion opens the piece picker and sends the chosen piece', () => {
    let received: { promotion?: string; sanitized: string } | null = null
    const { container, getByLabelText } = render(
      <Chessboard
        fen={PROMOTION_FEN}
        onMove={(_fen, details) => {
          received = details
        }}
      />
    )
    fireEvent.click(square(container, 'a7'))
    fireEvent.click(square(container, 'a8'))

    // Move must not execute until a piece is picked
    expect(received).toBeNull()

    fireEvent.click(getByLabelText('Promote to rook'))

    expect(received).not.toBeNull()
    expect(received!.promotion).toBe('r')
    expect(received!.sanitized).toContain('=R')
  })

  test('clicking the picker backdrop cancels the promotion', () => {
    let moved = false
    const { container } = render(
      <Chessboard fen={PROMOTION_FEN} onMove={() => { moved = true }} />
    )
    fireEvent.click(square(container, 'a7'))
    fireEvent.click(square(container, 'a8'))

    const backdrop = container.querySelector('.backdrop-blur-sm')
    if (!backdrop) throw new Error('promotion picker backdrop not rendered')
    fireEvent.click(backdrop)

    expect(moved).toBe(false)
    // The pawn is still on a7
    expect(square(container, 'a7').querySelector('svg')).not.toBeNull()
  })
})
