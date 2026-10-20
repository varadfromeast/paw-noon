// Port of System.Random(int Seed) as shipped in Unity (Mono / IL2CPP class libraries) and .NET's
// seeded compat implementation: Knuth's subtractive generator, including the original quirks
// (inextp starts at 21, InternalSample never returns MBIG, NextDouble multiplies by 1/MBIG).
// Every intermediate is wrapped to int32 with `| 0`, exactly like C#'s unchecked int arithmetic.
// Parity: Tools/Parity/vectors.json "schedules[].next" (raw Next() values) and "holdNs".

const MBIG = 2147483647; // int.MaxValue
const MSEED = 161803398;

export class NetRandom {
  private readonly seedArray = new Int32Array(56);
  private inext = 0;
  private inextp = 21;

  constructor(seed: number) {
    if (!Number.isInteger(seed) || seed < -2147483648 || seed > 2147483647) {
      throw new RangeError(`seed must be an int32, got ${seed}`);
    }
    const sa = this.seedArray;
    const subtraction = seed === -2147483648 ? MBIG : Math.abs(seed);
    let mj = (MSEED - subtraction) | 0;
    sa[55] = mj;
    let mk = 1;
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      sa[ii] = mk;
      mk = (mj - mk) | 0;
      if (mk < 0) mk = (mk + MBIG) | 0;
      mj = sa[ii];
    }
    for (let k = 1; k < 5; k++) {
      for (let i = 1; i < 56; i++) {
        let v = (sa[i] - sa[1 + ((i + 30) % 55)]) | 0;
        if (v < 0) v = (v + MBIG) | 0;
        sa[i] = v;
      }
    }
    this.inext = 0;
    this.inextp = 21;
  }

  private internalSample(): number {
    const sa = this.seedArray;
    let locINext = this.inext + 1;
    let locINextp = this.inextp + 1;
    if (locINext >= 56) locINext = 1;
    if (locINextp >= 56) locINextp = 1;
    let retVal = (sa[locINext] - sa[locINextp]) | 0;
    if (retVal === MBIG) retVal--;
    if (retVal < 0) retVal = (retVal + MBIG) | 0;
    sa[locINext] = retVal;
    this.inext = locINext;
    this.inextp = locINextp;
    return retVal;
  }

  /** Random.Next(): [0, int.MaxValue). */
  next(): number {
    return this.internalSample();
  }

  /** Random.NextDouble(): InternalSample() * (1.0 / MBIG). */
  nextDouble(): number {
    return this.internalSample() * (1.0 / MBIG);
  }
}
