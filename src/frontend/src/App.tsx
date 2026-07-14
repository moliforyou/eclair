import { useState, useEffect, useRef } from 'react'
import { Chess } from 'chess.js'
import { Chessboard } from './components/Chessboard'
import { Button } from './components/ui/Button'
import { Card, CardHeader, CardTitle } from './components/ui/Card'
import { Input } from './components/ui/Input'
import { squareToCoords, coordsToSquare } from './lib/utils'
import { HubConnectionBuilder, HubConnection } from '@microsoft/signalr'
import {
  Activity,
  Cpu,
  Layers,
  Terminal as TerminalIcon,
  RefreshCw,
  Volume2,
  VolumeX,
  Grid,
  Link2,
  User
} from 'lucide-react'

interface MoveRecord {
  num: number
  white: string
  black?: string
  whiteTelemetry: string
  blackTelemetry?: string
}

interface ServerMoveDto {
  From: { Item1: number; Item2: number }
  To: { Item1: number; Item2: number }
  Promotion: string | null
}

function App() {
  const [screen, setScreen] = useState<'landing' | 'game'>('landing')
  const [gameId, setGameId] = useState<string | null>(null)
  const [playerId, setPlayerId] = useState(() => {
    const saved = localStorage.getItem('chess_player_id')
    if (saved) return saved
    const fresh = 'player_' + Math.floor(Math.random() * 1000)
    localStorage.setItem('chess_player_id', fresh)
    return fresh
  })
  const [whitePlayerId, setWhitePlayerId] = useState<string>('')
  const [blackPlayerId, setBlackPlayerId] = useState<string>('')
  const [isQueued, setIsQueued] = useState(false)
  const [connectionStatus, setConnectionStatus] = useState<'disconnected' | 'connecting' | 'connected'>('disconnected')
  const [gameStatus, setGameStatus] = useState<string>('InProgress')
  
  // Fresh standard game setup
  const [fen, setFen] = useState('rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1')
  const [game, setGame] = useState(() => new Chess())
  const [moves, setMoves] = useState<MoveRecord[]>([])
  const [command, setCommand] = useState('')
  const [commandError, setCommandError] = useState<string | null>(null)
  const [soundEnabled, setSoundEnabled] = useState(true)
  const [latency, setLatency] = useState(12)
  const [hashrate, setHashrate] = useState(2450)
  
  const ledgerEndRef = useRef<HTMLDivElement>(null)
  const connectionRef = useRef<HubConnection | null>(null)

  // Sync state and establish SignalR connection when gameId or playerId changes
  useEffect(() => {
    let isMounted = true

    // Helper to safely extract coordinates from various tuple/object structures
    const getPosCoords = (pos: unknown): { row: number, col: number } | null => {
      if (!pos) return null;
      if (Array.isArray(pos)) {
        return { col: pos[0] as number, row: pos[1] as number };
      }
      if (typeof pos === 'object') {
        const obj = pos as Record<string, unknown>;
        const col = ('Item1' in obj) ? obj.Item1 : ('item1' in obj) ? obj.item1 : null;
        const row = ('Item2' in obj) ? obj.Item2 : ('item2' in obj) ? obj.item2 : null;
        if (typeof col === 'number' && typeof row === 'number') {
          return { col, row };
        }
      }
      return null;
    }

    // Helper to rebuild move records from a server Moves array
    const buildMovesFromServer = (serverMoves: ServerMoveDto[]): MoveRecord[] => {
      // Replay all moves on a fresh board to get algebraic notation
      const tempGame = new Chess()
      const algMoves: string[] = []
      for (const m of serverMoves) {
        try {
          const fromCoords = getPosCoords(m.From);
          const toCoords = getPosCoords(m.To);
          if (!fromCoords || !toCoords) continue;

          const result = tempGame.move({
            from: coordsToSquare(fromCoords.row, fromCoords.col),
            to: coordsToSquare(toCoords.row, toCoords.col),
            promotion: m.Promotion ? m.Promotion.toLowerCase() : undefined,
          })
          if (result) algMoves.push(result.san)
        } catch {
          // skip unrecognised moves
        }
      }
      const records: MoveRecord[] = []
      for (let i = 0; i < algMoves.length; i += 2) {
        records.push({
          num: Math.floor(i / 2) + 1,
          white: algMoves[i],
          black: algMoves[i + 1] || undefined,
          whiteTelemetry: `eval: +0.15 | t: 3ms`,
          blackTelemetry: algMoves[i + 1] ? `eval: -0.10 | t: 4ms` : undefined,
        })
      }
      return records
    }

    // Helper to update state from a FEN string (for initial HTTP sync)
    const updateGameState = (newFen: string, serverMoves?: ServerMoveDto[], status?: string, white?: string, black?: string) => {
      setFen(newFen)
      const localGame = new Chess(newFen)
      setGame(localGame)
      setGameStatus(status ?? 'InProgress')
      if (white) setWhitePlayerId(white)
      if (black) setBlackPlayerId(black)

      if (serverMoves && serverMoves.length > 0) {
        setMoves(buildMovesFromServer(serverMoves))
      } else {
        // Fallback: no server move history available
        setMoves([])
      }
    }

    // 1. Initial HTTP sync with database
    const syncGame = async () => {
      if (!gameId) return
      try {
        const getRes = await fetch(`/api/games/${gameId}`)
        if (getRes.status === 404) {
          // Game does not exist yet, call create endpoint to initialize it in Postgres
          const createRes = await fetch(`/api/games/${gameId}/create`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ WhitePlayerId: playerId, BlackPlayerId: 'computer' })
          })
          if (createRes.ok) {
            const freshRes = await fetch(`/api/games/${gameId}`)
            if (freshRes.ok && isMounted) {
              const data = await freshRes.json()
              updateGameState(data.fen, data.moves, data.status, data.whitePlayerId, data.blackPlayerId)
            }
          }
        } else if (getRes.ok && isMounted) {
          const data = await getRes.json()
          updateGameState(data.fen, data.moves, data.status, data.whitePlayerId, data.blackPlayerId)
        }
      } catch (err) {
        console.error('Failed to sync game state with backend:', err)
      }
    }

    if (gameId) {
      syncGame()
    }

    // 2. Establish SignalR Connection
    setTimeout(() => {
      if (isMounted) {
        setConnectionStatus('connecting')
      }
    }, 0)

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/game')
      .withAutomaticReconnect()
      .build()

    connection.on('MovePlayed', (data: { fen: string, turn: 'w' | 'b', whitePlayerId: string, blackPlayerId: string, moves: ServerMoveDto[], status: string }) => {
      if (isMounted) {
        // Received real-time move broadcast from backend!
        updateGameState(data.fen, data.moves, data.status, data.whitePlayerId, data.blackPlayerId)
      }
    })

    connection.on('MatchFound', (data: { gameId: string, role: string, whitePlayerId: string, blackPlayerId: string }) => {
      if (isMounted) {
        console.log('MatchFound event received:', data)
        setIsQueued(false)
        // Join the new game group on the existing connection before teardown
        connection.invoke('JoinGame', data.gameId).catch(console.error)
        setGameId(data.gameId)
        setScreen('game')
        // The gameId state change will trigger re-render and re-sync
      }
    })

    connection.start()
      .then(() => {
        if (!isMounted) {
          connection.stop()
          return
        }
        setConnectionStatus('connected')
        if (gameId) {
          connection.invoke('JoinGame', gameId)
        }
        connection.invoke('JoinLobby', playerId)
      })
      .catch((err) => {
        console.error('SignalR Hub Connection Error:', err)
        if (isMounted) setConnectionStatus('disconnected')
      })

    connectionRef.current = connection

    return () => {
      isMounted = false
      if (connectionRef.current) {
        connectionRef.current.stop()
      }
    }
  }, [gameId, playerId])

  // Simulate telemetry latency metrics
  useEffect(() => {
    const timer = setInterval(() => {
      setLatency(prev => {
        const offset = Math.floor(Math.random() * 5) - 2
        return Math.max(8, Math.min(25, prev + offset))
      })
      setHashrate(prev => {
        const offset = Math.floor(Math.random() * 100) - 50
        return Math.max(2200, Math.min(2700, prev + offset))
      })
    }, 3000)
    return () => clearInterval(timer)
  }, [])

  // Auto-scroll the move ledger
  useEffect(() => {
    ledgerEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [moves])

  const generateTelemetry = () => {
    const ev = (Math.random() * 2 - 1).toFixed(2)
    const sign = parseFloat(ev) >= 0 ? '+' : ''
    const time = Math.floor(Math.random() * 8) + 2
    return `eval: ${sign}${ev} | t: ${time}ms`
  }

  // Handle board move updates (with client prediction + HTTP server push)
  const handleBoardMove = async (newFen: string, moveDetails?: { from: string; to: string; sanitized: string }) => {
    if (!moveDetails) return

    // 1. Client prediction: append the move to existing records instead of rebuilding from FEN
    setFen(newFen)
    const localGame = new Chess(newFen)
    setGame(localGame)
    // Append a client-predicted move entry (server will confirm via SignalR MovePlayed)
    setMoves(prev => {
      const san = moveDetails.sanitized || moveDetails.from + moveDetails.to
      const lastRecord = prev[prev.length - 1]
      const needsNewRecord = !lastRecord || lastRecord.black !== undefined
      if (needsNewRecord) {
        return [...prev, {
          num: prev.length + 1,
          white: san,
          black: undefined,
          whiteTelemetry: generateTelemetry(),
        }]
      } else {
        // Fill in black's move on the last record
        return prev.slice(0, -1).concat([{
          ...lastRecord,
          black: san,
          blackTelemetry: generateTelemetry(),
        }])
      }
    })
    setCommandError(null)

    // 2. Push move to server
    const fromCoords = squareToCoords(moveDetails.from)
    const toCoords = squareToCoords(moveDetails.to)

    try {
      const res = await fetch(`/api/games/${gameId}/move`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-User-Id': playerId
        },
        body: JSON.stringify({
          FromRow: fromCoords.row,
          FromCol: fromCoords.col,
          ToRow: toCoords.row,
          ToCol: toCoords.col,
          Promotion: 'Q' // Auto-promote to Queen for UI simplicity
        })
      })

      if (!res.ok) {
        const errorData = await res.json()
        setCommandError(errorData.Error || 'Move rejected by server')
        
        // Rollback: Fetch current state from server
        const syncRes = await fetch(`/api/games/${gameId}`)
        if (syncRes.ok) {
          const data = await syncRes.json()
          setFen(data.fen)
          const syncGameInstance = new Chess(data.fen)
          setGame(syncGameInstance)
        }
        setTimeout(() => setCommandError(null), 4000)
      }
    } catch (err) {
      console.error('Failed to submit move:', err)
      setCommandError('Network error: failed to submit move to server')
      
      // Rollback
      const syncRes = await fetch(`/api/games/${gameId}`)
      if (syncRes.ok) {
        const data = await syncRes.json()
        setFen(data.fen)
        const syncGameInstance = new Chess(data.fen)
        setGame(syncGameInstance)
      }
      setTimeout(() => setCommandError(null), 4000)
    }
  }

  // Restart match (calls backend recreate to reset state in Postgres)
  const handleRestart = async () => {
    try {
      const res = await fetch(`/api/games/${gameId}/create`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ WhitePlayerId: playerId, BlackPlayerId: 'computer' })
      })
      if (res.ok) {
        setCommand('')
        setCommandError(null)
        setGameStatus('InProgress')
      } else {
        console.error('Failed to restart match on server')
      }
    } catch (err) {
      console.error('Error restarting match:', err)
    }
  }

  // Join matchmaking queue to be paired with another human player
  const handleFindMatch = async () => {
    try {
      setIsQueued(true)
      const res = await fetch('/api/matchmaking', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-User-Id': playerId
        },
        body: JSON.stringify({ Elo: 1200 })
      })
      if (!res.ok) {
        setIsQueued(false)
        console.error('Failed to enqueue for matchmaking')
      }
    } catch (err) {
      setIsQueued(false)
      console.error('Error joining matchmaking queue:', err)
    }
  }

  // Start game vs Computer
  const handlePlayComputer = () => {
    const newGameId = 'single_' + Math.floor(Math.random() * 1000000)
    setGameId(newGameId)
    setScreen('game')
  }

  // Leave active game and return to landing page
  const handleLeaveGame = () => {
    setGameId(null)
    setScreen('landing')
    setFen('rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1')
    setGame(new Chess())
    setMoves([])
    setGameStatus('InProgress')
    setWhitePlayerId('')
    setBlackPlayerId('')
  }

  // Cancel matchmaking search
  const handleCancelMatch = () => {
    setIsQueued(false)
  }

  // Command input handler (Algebraic chess terminal commands, e.g. e4, Nf3)
  const handleSendCommand = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!command.trim()) return

    const sanitizedCmd = command.trim()

    try {
      // Clone game state to test move
      const tempGame = new Chess(game.fen())
      const result = tempGame.move(sanitizedCmd)
      
      if (result) {
        // Valid move predicted! Apply locally
        const nextFen = tempGame.fen()
        setFen(nextFen)
        setGame(tempGame)

        // Append the new move to the existing records (server will confirm via MovePlayed)
        setMoves(prev => {
          const san = result.san
          const lastRecord = prev[prev.length - 1]
          const needsNewRecord = !lastRecord || lastRecord.black !== undefined
          if (needsNewRecord) {
            return [...prev, {
              num: prev.length + 1,
              white: san,
              black: undefined,
              whiteTelemetry: generateTelemetry(),
            }]
          } else {
            return prev.slice(0, -1).concat([{
              ...lastRecord,
              black: san,
              blackTelemetry: generateTelemetry(),
            }])
          }
        })
        setCommand('')
        setCommandError(null)

        // Convert algebraic letters/numbers to matrix rows and columns
        const fromCoords = squareToCoords(result.from)
        const toCoords = squareToCoords(result.to)

        // Submit move to backend
        const res = await fetch(`/api/games/${gameId}/move`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-User-Id': playerId
          },
          body: JSON.stringify({
            FromRow: fromCoords.row,
            FromCol: fromCoords.col,
            ToRow: toCoords.row,
            ToCol: toCoords.col,
            Promotion: result.promotion ? result.promotion.toUpperCase() : ''
          })
        })

        if (!res.ok) {
          const errorData = await res.json()
          setCommandError(errorData.Error || 'Move rejected by server')
          
          // Rollback
          const syncRes = await fetch(`/api/games/${gameId}`)
          if (syncRes.ok) {
            const data = await syncRes.json()
            setFen(data.fen)
            setGame(new Chess(data.fen))
          }
          setTimeout(() => setCommandError(null), 4000)
        }
      }
    } catch {
      setCommandError(`INVALID ENGINE COMMAND: "${sanitizedCmd}" is not a valid algebraic move. Try "e4", "Nf3", "O-O".`)
      setTimeout(() => setCommandError(null), 4000)
    }
  }

  return (
    <div className="relative min-h-screen bg-void text-pure-light flex flex-col items-center select-none overflow-hidden font-body">
      
      {/* Background Ambience elements */}
      <div className="absolute inset-0 bg-grid-pattern z-0 opacity-40" />
      
      {/* Absolute ambient light blobs */}
      <div className="absolute top-1/4 left-1/4 w-[350px] h-[350px] rounded-full bg-btc-orange/10 blur-[130px] z-0 pointer-events-none" />
      <div className="absolute bottom-1/4 right-1/4 w-[400px] h-[400px] rounded-full bg-gold/5 blur-[150px] z-0 pointer-events-none" />

      {/* Main Glass Header */}
      <header className="relative w-full border-b border-pure-light/10 bg-matter/40 backdrop-blur-md z-10">
        <div className="max-w-7xl mx-auto px-6 h-16 flex items-center justify-between">
          
          {/* Logo Node with Spinning Orbital Rings */}
          <div className="flex items-center gap-3">
            <div className="relative w-9 h-9 flex items-center justify-center">
              <div className="absolute w-full h-full border border-dashed border-btc-orange/40 rounded-full animate-spin-slow" />
              <div className="absolute w-7 h-7 border border-dashed border-gold/40 rounded-full animate-spin-slow-reverse" />
              <div className="w-3.5 h-3.5 bg-gradient-to-r from-burnt-orange to-gold rounded-full shadow-[0_0_12px_rgba(247,147,26,0.6)]" />
            </div>
            <div className="flex flex-col">
              <span className="font-heading font-bold text-md leading-none tracking-wider text-pure-light">
                ECLAIR<span className="bg-gradient-to-r from-btc-orange to-gold bg-clip-text text-transparent ml-1">CORE</span>
              </span>
              <span className="font-mono text-[9px] tracking-widest text-stardust/60 uppercase">
                Chess Platform Terminal
              </span>
            </div>
          </div>

          {/* Glowing Status badge */}
          <div className="flex items-center gap-6">
            <div className="hidden md:flex items-center gap-2 px-3 py-1 border border-pure-light/15 rounded-full bg-matter/60 font-mono text-[10px]">
              {connectionStatus === 'connected' && (
                <>
                  <span className="relative flex h-2 w-2">
                    <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-gold opacity-75"></span>
                    <span className="relative inline-flex rounded-full h-2 w-2 bg-gold"></span>
                  </span>
                  <span className="text-stardust uppercase">SECURE ENGINE LINK: OK</span>
                </>
              )}
              {connectionStatus === 'connecting' && (
                <>
                  <span className="relative flex h-2 w-2">
                    <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-btc-orange opacity-75"></span>
                    <span className="relative inline-flex rounded-full h-2 w-2 bg-btc-orange animate-pulse"></span>
                  </span>
                  <span className="text-btc-orange uppercase">ESTABLISHING LINK...</span>
                </>
              )}
              {connectionStatus === 'disconnected' && (
                <>
                  <span className="relative flex h-2 w-2">
                    <span className="relative inline-flex rounded-full h-2 w-2 bg-red-500"></span>
                  </span>
                  <span className="text-red-500 uppercase">ENGINE DISCONNECTED</span>
                </>
              )}
            </div>

            {/* Audio Toggle */}
            <button
              onClick={() => setSoundEnabled(!soundEnabled)}
              className="p-2 border border-pure-light/10 rounded-full hover:border-btc-orange/50 hover:text-btc-orange transition-all duration-300 cursor-pointer"
              title={soundEnabled ? 'Mute Interface Sounds' : 'Unmute Interface Sounds'}
            >
              {soundEnabled ? <Volume2 size={15} /> : <VolumeX size={15} />}
            </button>
          </div>
        </div>
      </header>

      {/* Main Terminal Body / Screen Router */}
      {screen === 'landing' ? (
        <main className="relative max-w-4xl w-full mx-auto px-4 py-12 flex-grow z-10 flex flex-col justify-center items-center gap-8">
          {/* Logo & Intro */}
          <div className="text-center space-y-4">
            <h1 className="font-heading font-bold text-4xl md:text-6xl tracking-tight leading-tight m-0 text-pure-light">
              ECLAIR<span className="bg-gradient-to-r from-btc-orange to-gold bg-clip-text text-transparent ml-1">CORE</span>
            </h1>
            <p className="font-mono text-xs md:text-sm tracking-widest text-stardust/80 uppercase">
              Next-Gen Neural Chess Platform
            </p>
          </div>

          <Card variant="glass" className="w-full max-w-xl p-8 space-y-6 backdrop-blur-md relative overflow-hidden" withCornerAccents hoverEffect={false}>
            {/* Ambient decorative nodes */}
            <div className="absolute top-0 right-0 w-16 h-16 border-t border-r border-btc-orange/10 pointer-events-none" />
            <div className="absolute bottom-0 left-0 w-16 h-16 border-b border-l border-gold/10 pointer-events-none" />

            <div className="space-y-4">
              <div className="flex items-center gap-3 text-gold font-mono border-b border-pure-light/5 pb-2">
                <User size={18} className="text-btc-orange" />
                <span className="text-sm uppercase tracking-wider text-pure-light font-bold">Configure Player Identity</span>
              </div>
              <p className="text-xs text-stardust">
                Your player ID is used to track active games and pair you in the matchmaking system.
              </p>
              <Input
                value={playerId}
                onChange={(e) => {
                  const val = e.target.value
                  setPlayerId(val)
                  localStorage.setItem('chess_player_id', val)
                }}
                placeholder="Enter Player User ID"
                className="h-11 text-sm bg-black/40 border-pure-light/20 text-pure-light focus:border-btc-orange"
              />
            </div>

            <div className="border-t border-pure-light/5 pt-6 space-y-4">
              <div className="flex items-center gap-3 text-gold font-mono border-b border-pure-light/5 pb-2">
                <Grid size={18} className="text-gold" />
                <span className="text-sm uppercase tracking-wider text-pure-light font-bold">Choose Game Mode</span>
              </div>

              {isQueued ? (
                <div className="flex flex-col items-center justify-center p-6 bg-black/30 border border-btc-orange/20 rounded-xl space-y-4 animate-pulse">
                  <RefreshCw size={24} className="text-btc-orange animate-spin" />
                  <div className="text-center space-y-1">
                    <p className="text-sm font-bold text-pure-light">Matching with an Opponent...</p>
                    <p className="text-[10px] text-stardust/60 font-mono">YOUR ID: {playerId}</p>
                  </div>
                  <Button variant="outline" size="sm" onClick={handleCancelMatch} className="h-9 px-4">
                    Cancel Search
                  </Button>
                </div>
              ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  <Card 
                    variant="default" 
                    className="p-5 flex flex-col justify-between items-start gap-4 bg-matter/40 hover:border-gold/40 border border-pure-light/10 transition-all duration-300 cursor-pointer"
                    onClick={handlePlayComputer}
                  >
                    <div className="space-y-1">
                      <h4 className="text-sm font-bold text-pure-light uppercase tracking-wider">VS Neural Engine</h4>
                      <p className="text-xs text-stardust">Practice against the computer player instantly.</p>
                    </div>
                    <Button variant="outline" size="sm" className="w-full text-[10px] h-9">
                      Start Game
                    </Button>
                  </Card>

                  <Card 
                    variant="default" 
                    className="p-5 flex flex-col justify-between items-start gap-4 bg-matter/40 hover:border-btc-orange/40 border border-pure-light/10 transition-all duration-300 cursor-pointer"
                    onClick={handleFindMatch}
                  >
                    <div className="space-y-1">
                      <h4 className="text-sm font-bold text-pure-light uppercase tracking-wider">VS Human Player</h4>
                      <p className="text-xs text-stardust">Join matchmaking queue to find an online opponent.</p>
                    </div>
                    <Button variant="primary" size="sm" className="w-full text-[10px] h-9">
                      Find Opponent
                    </Button>
                  </Card>
                </div>
              )}
            </div>
          </Card>

          {/* Cyber Technical Status info on landing page */}
          <div className="flex gap-6 text-[10px] font-mono text-stardust/50 tracking-wider">
            <span className="flex items-center gap-1.5">
              <span className={`w-1.5 h-1.5 rounded-full ${connectionStatus === 'connected' ? 'bg-gold animate-ping' : 'bg-red-500'}`} />
              LINK: {connectionStatus.toUpperCase()}
            </span>
            <span>LATENCY: {latency}ms</span>
          </div>
        </main>
      ) : (
        <main className="relative max-w-7xl w-full mx-auto px-4 py-8 flex-grow z-10 flex flex-col gap-6">
          
          {/* Banner with dramatic technical chess typography */}
          <div className="text-center md:text-left flex flex-col md:flex-row md:items-end justify-between border-b border-pure-light/5 pb-4">
            <div>
              <h1 className="font-heading font-bold text-3xl md:text-5xl tracking-tight leading-tight m-0 text-pure-light">
                Secure Neural <span className="bg-gradient-to-r from-btc-orange to-gold bg-clip-text text-transparent">Chess Core</span>
              </h1>
              <p className="font-body text-sm md:text-base text-stardust mt-1">
                Zero-latency client predicted move validation. Enter keyboard algebraic commands or select pieces.
              </p>
            </div>
            <div className="flex gap-2 mt-4 md:mt-0 justify-center">
              {blackPlayerId === 'computer' && (
                <Button variant="outline" size="sm" onClick={handleRestart} className="gap-2">
                  <RefreshCw size={13} /> Reset Game
                </Button>
              )}
              <Button variant="outline" size="sm" onClick={handleLeaveGame} className="gap-2">
                Leave Game
              </Button>
            </div>
          </div>

          {/* Active Game Info Dashboard */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <Card variant="glass" className="p-4 flex justify-between items-center" hoverEffect={false}>
              <div className="flex items-center gap-3 text-btc-orange font-mono">
                <Link2 size={16} />
                <div>
                  <span className="text-[10px] uppercase tracking-wider text-stardust block">Game Match ID</span>
                  <span className="text-xs font-bold text-pure-light">{gameId}</span>
                </div>
              </div>
              <div className="px-2.5 py-1 rounded-full border border-btc-orange/20 bg-btc-orange/10 font-mono text-[9px] text-btc-orange uppercase">
                {blackPlayerId === 'computer' ? 'Singleplayer vs CPU' : 'Multiplayer vs Human'}
              </div>
            </Card>

            <Card variant="glass" className="p-4 flex justify-between items-center" hoverEffect={false}>
              <div className="flex items-center gap-3 text-gold font-mono">
                <User size={16} />
                <div>
                  <span className="text-[10px] uppercase tracking-wider text-stardust block">Matchup Profile</span>
                  <span className="text-xs font-bold text-pure-light">
                    <span className={playerId === whitePlayerId ? 'text-gold' : 'text-pure-light'}>{whitePlayerId || 'Waiting...'}</span>
                    <span className="text-stardust/40 mx-2">VS</span>
                    <span className={playerId === blackPlayerId ? 'text-gold' : 'text-pure-light'}>{blackPlayerId || 'Waiting...'}</span>
                  </span>
                </div>
              </div>
              {playerId && (whitePlayerId === playerId || blackPlayerId === playerId) && (
                <div className="px-2.5 py-1 rounded-full border border-gold/20 bg-gold/10 font-mono text-[9px] text-gold uppercase">
                  Playing as {playerId === whitePlayerId ? 'White' : 'Black'}
                </div>
              )}
            </Card>
          </div>

          {/* Dynamic Column Split */}
          <div className="grid grid-cols-1 lg:grid-cols-12 gap-8">
            
            {/* Chessboard Area (7 Cols) */}
            <div className="lg:col-span-7 flex flex-col items-center">
              <Card className="w-full flex justify-center py-8 px-4 md:px-8 border-pure-light/10 bg-matter/40 backdrop-blur-md holographic-gradient relative overflow-hidden" withCornerAccents hoverEffect={false}>
                
                {/* Subtle visual background grid details */}
                <div className="absolute top-0 right-0 w-32 h-32 border-t border-r border-pure-light/5 pointer-events-none" />
                <div className="absolute bottom-0 left-0 w-32 h-32 border-b border-l border-pure-light/5 pointer-events-none" />

                <Chessboard
                  key={fen}
                  fen={fen}
                  onMove={handleBoardMove}
                  disabled={false}
                />
                {/* Game-over overlay */}
                {gameStatus && gameStatus !== 'InProgress' && gameStatus !== 'Check' && (
                  <div className="absolute inset-0 flex flex-col items-center justify-center bg-black/70 backdrop-blur-sm rounded-xl z-20">
                    <div className="text-center space-y-3 px-8">
                      <div className="text-4xl">
                        {gameStatus.startsWith('Checkmate') ? '♚' : gameStatus === 'Stalemate' ? '⚖️' : '🤝'}
                      </div>
                      <h2 className="font-heading font-bold text-2xl text-pure-light">
                        {gameStatus.startsWith('Checkmate:White') ? 'White Wins!' :
                         gameStatus.startsWith('Checkmate:Black') ? 'Black Wins!' :
                         gameStatus === 'Stalemate' ? 'Stalemate' :
                         gameStatus.startsWith('Draw') ? 'Draw' : 'Game Over'}
                      </h2>
                      <p className="text-stardust text-sm font-mono">
                        {gameStatus.startsWith('Checkmate') ? 'Checkmate — the game is over.' :
                         gameStatus === 'Stalemate' ? 'No legal moves — stalemate.' :
                         gameStatus.replace('Draw:', 'Draw: ')}
                      </p>
                      <Button variant="primary" size="sm" onClick={handleRestart} className="mt-2">
                        <RefreshCw size={13} /> Play Again
                      </Button>
                    </div>
                  </div>
                )}
              </Card>

              {/* algebraic Terminal Console Bar (Bottom of Board) */}
              <div className="w-full mt-4">
                <form onSubmit={handleSendCommand} className="flex gap-3">
                  <Input
                    value={command}
                    onChange={(e) => setCommand(e.target.value)}
                    placeholder="ENTER ALGEBRAIC MOVE e.g. 'e4', 'Nf3', 'd4'..."
                    withPrefix={<TerminalIcon size={14} />}
                    className="flex-grow"
                  />
                  <Button type="submit" variant="primary">
                    EXECUTE MOVE
                  </Button>
                </form>
                
                {/* Command Errors warning */}
                {commandError && (
                  <div className="mt-2.5 px-3.5 py-2 border border-red-500/20 bg-red-950/20 rounded-lg text-xs font-mono text-red-400 animate-pulse">
                    {commandError}
                  </div>
                )}
              </div>
            </div>

            {/* Live Node Statistics & Move Log Sidebar (5 Cols) */}
            <div className="lg:col-span-5 flex flex-col gap-6">
              
              {/* Live Metrics Grid */}
              <div className="grid grid-cols-2 gap-4">
                <Card variant="glass" className="p-4" hoverEffect>
                  <div className="flex justify-between items-start">
                    <div className="flex flex-col">
                      <span className="text-[10px] font-mono text-stardust uppercase tracking-wider">Node Latency</span>
                      <span className="font-mono text-lg font-bold text-gold mt-1">{latency} ms</span>
                    </div>
                    <div className="p-1.5 border border-gold/20 bg-gold/10 rounded-lg text-gold">
                      <Activity size={14} />
                    </div>
                  </div>
                  <div className="text-[9px] font-mono text-stardust/60 mt-2">Zero-latency Client Prediction Active</div>
                </Card>

                <Card variant="glass" className="p-4" hoverEffect>
                  <div className="flex justify-between items-start">
                    <div className="flex flex-col">
                      <span className="text-[10px] font-mono text-stardust uppercase tracking-wider">Engine Nodes</span>
                      <span className="font-mono text-lg font-bold text-btc-orange mt-1">{(hashrate / 1000).toFixed(2)} M/s</span>
                    </div>
                    <div className="p-1.5 border border-btc-orange/20 bg-btc-orange/10 rounded-lg text-btc-orange">
                      <Cpu size={14} />
                    </div>
                  </div>
                  <div className="text-[9px] font-mono text-stardust/60 mt-2">chess.js local thread active</div>
                </Card>
              </div>

              {/* Live Move Telemetry Ledger */}
              <Card variant="glass" className="flex-grow flex flex-col h-[350px] lg:h-[400px] p-0 overflow-hidden border-pure-light/10 bg-matter/40 backdrop-blur-md" hoverEffect={false}>
                <CardHeader className="p-4 border-b border-pure-light/5 flex flex-row items-center justify-between">
                  <div className="flex items-center gap-2">
                    <Layers size={14} className="text-btc-orange" />
                    <CardTitle className="text-sm font-mono tracking-wider uppercase font-semibold">MOVE TELEMETRY LEDGER</CardTitle>
                  </div>
                  <span className="font-mono text-[9px] text-stardust/50 px-2 py-0.5 border border-pure-light/5 bg-black/30 rounded-full">
                    PLIES PLAYED: {moves.length * 2}
                  </span>
                </CardHeader>
                
                {/* Scrollable list of moves */}
                <div className="flex-grow overflow-y-auto p-4 space-y-3 font-mono text-xs">
                  {moves.length === 0 ? (
                    <div className="h-full flex flex-col items-center justify-center text-stardust/40 space-y-2">
                      <Grid size={24} className="animate-pulse" />
                      <span className="text-[10px] uppercase tracking-wider">No moves recorded in telemetry ledger</span>
                    </div>
                  ) : (
                    moves.map((m) => (
                      <div
                        key={m.num}
                        className="group border border-pure-light/5 bg-black/20 rounded-xl p-3 flex flex-col gap-2 hover:border-btc-orange/30 transition-all duration-300"
                      >
                        {/* Move sequence tag */}
                        <div className="flex justify-between items-center text-[10px] text-stardust/50 border-b border-pure-light/5 pb-1">
                          <span>MOVE SEQUENCE #{m.num.toString().padStart(3, '0')}</span>
                          <span className="text-gold font-semibold uppercase">VALIDATED BY ENGINE</span>
                        </div>

                        {/* Side by side player moves */}
                        <div className="grid grid-cols-2 gap-4">
                          {/* White move */}
                          <div className="flex flex-col gap-0.5">
                            <span className="text-[9px] text-stardust/40 uppercase">WHITE MOVE TELEMETRY</span>
                            <div className="flex items-center justify-between">
                              <span className="text-gold font-bold text-sm">{m.white}</span>
                              <span className="text-[8px] text-stardust/60 font-mono">{m.whiteTelemetry}</span>
                            </div>
                          </div>

                          {/* Black move */}
                          {m.black ? (
                            <div className="flex flex-col gap-0.5 border-l border-pure-light/5 pl-4">
                              <span className="text-[9px] text-stardust/40 uppercase">BLACK MOVE TELEMETRY</span>
                              <div className="flex items-center justify-between">
                                <span className="text-btc-orange font-bold text-sm">{m.black}</span>
                                <span className="text-[8px] text-stardust/60 font-mono">{m.blackTelemetry}</span>
                              </div>
                            </div>
                          ) : (
                            <div className="flex items-center pl-4 text-[9px] text-stardust/30 animate-pulse uppercase tracking-wider">
                              Pending Move...
                            </div>
                          )}
                        </div>
                      </div>
                    ))
                  )}
                  <div ref={ledgerEndRef} />
                </div>
              </Card>
            </div>
          </div>
        </main>
      )}

      {/* Cyber Technical Ticker Footer */}
      <footer className="w-full border-t border-pure-light/10 bg-matter/20 mt-12 py-4 text-center font-mono text-[9px] text-stardust/40 tracking-wider">
        <div className="max-w-7xl mx-auto px-6 flex flex-col sm:flex-row items-center justify-between gap-2">
          <span>ECLAIR SYSTEM NODE v1.0.0-PROTOTYPE</span>
          <span className="flex items-center gap-1.5">
            <span className="w-1.5 h-1.5 rounded-full bg-gold animate-ping" />
            LIVE TELEMETRY BROADCAST ACTIVE
          </span>
        </div>
      </footer>
    </div>
  )
}

export default App
