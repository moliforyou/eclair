import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

export function squareToCoords(square: string) {
  return {
    col: square.charCodeAt(0) - 97,
    row: parseInt(square.charAt(1)) - 1
  }
}
