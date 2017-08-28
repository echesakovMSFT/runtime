// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// 
////////////////////////////////////////////////////////////////////////////
//
//
//  Purpose:  This class is called by TimeSpan to parse a time interval string.
//
//  Standard Format:
//  -=-=-=-=-=-=-=-
//  "c":  Constant format.  [-][d'.']hh':'mm':'ss['.'fffffff]  
//  Not culture sensitive.  Default format (and null/empty format string) map to this format.
//
//  "g":  General format, short:  [-][d':']h':'mm':'ss'.'FFFFFFF  
//  Only print what's needed.  Localized (if you want Invariant, pass in Invariant).
//  The fractional seconds separator is localized, equal to the culture's DecimalSeparator.
//
//  "G":  General format, long:  [-]d':'hh':'mm':'ss'.'fffffff
//  Always print days and 7 fractional digits.  Localized (if you want Invariant, pass in Invariant).
//  The fractional seconds separator is localized, equal to the culture's DecimalSeparator.
//
//
//  * "TryParseTimeSpan" is the main method for Parse/TryParse
//
//  - TimeSpanTokenizer.GetNextToken() is used to split the input string into number and literal tokens.
//  - TimeSpanRawInfo.ProcessToken() adds the next token into the parsing intermediary state structure
//  - ProcessTerminalState() uses the fully initialized TimeSpanRawInfo to find a legal parse match.
//    The terminal states are attempted as follows:
//    foreach (+InvariantPattern, -InvariantPattern, +LocalizedPattern, -LocalizedPattern) try
//       1 number  => d
//       2 numbers => h:m
//       3 numbers => h:m:s     | d.h:m   | h:m:.f
//       4 numbers => h:m:s.f   | d.h:m:s | d.h:m:.f
//       5 numbers => d.h:m:s.f
//
// Custom Format:
// -=-=-=-=-=-=-=
//
// * "TryParseExactTimeSpan" is the main method for ParseExact/TryParseExact methods
// * "TryParseExactMultipleTimeSpan" is the main method for ParseExact/TryparseExact
//    methods that take a String[] of formats
//
// - For single-letter formats "TryParseTimeSpan" is called (see above)
// - For multi-letter formats "TryParseByFormat" is called
// - TryParseByFormat uses helper methods (ParseExactLiteral, ParseExactDigits, etc)
//   which drive the underlying TimeSpanTokenizer.  However, unlike standard formatting which
//   operates on whole-tokens, ParseExact operates at the character-level.  As such, 
//   TimeSpanTokenizer.NextChar and TimeSpanTokenizer.BackOne() are called directly. 
//
////////////////////////////////////////////////////////////////////////////

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Globalization
{
    internal static class TimeSpanParse
    {
        // ---- SECTION:  members for internal support ---------*
        internal const int unlimitedDigits = -1;
        internal const int maxFractionDigits = 7;

        internal const int maxDays = 10675199;
        internal const int maxHours = 23;
        internal const int maxMinutes = 59;
        internal const int maxSeconds = 59;
        internal const int maxFraction = 9999999;

        #region InternalSupport
        private enum ParseFailureKind : byte
        {
            None = 0,
            ArgumentNull = 1,
            Format = 2,
            FormatWithParameter = 3,
            Overflow = 4,
        }

        [Flags]
        private enum TimeSpanStandardStyles : byte
        {     // Standard Format Styles
            None = 0x00000000,
            Invariant = 0x00000001, //Allow Invariant Culture
            Localized = 0x00000002, //Allow Localized Culture
            RequireFull = 0x00000004, //Require the input to be in DHMSF format
            Any = Invariant | Localized,
        }

        // TimeSpan Token Types
        private enum TTT : byte
        {
            None = 0,    // None of the TimeSpanToken fields are set
            End = 1,    // '\0'
            Num = 2,    // Number
            Sep = 3,    // literal
            NumOverflow = 4,    // Number that overflowed
        }

        [IsByRefLike]
        private struct TimeSpanToken
        {
            internal TTT ttt;
            internal int num;           // Store the number that we are parsing (if any)
            internal int zeroes;        // Store the number of leading zeroes (if any)
            internal ReadOnlySpan<char> sep;        // Store the literal that we are parsing (if any)

            public TimeSpanToken(TTT type) : this(type, 0, 0, default(ReadOnlySpan<char>)) { }

            public TimeSpanToken(int number) : this(TTT.Num, number, 0, default(ReadOnlySpan<char>)) { }

            public TimeSpanToken(int number, int leadingZeroes) : this(TTT.Num, number, leadingZeroes, default(ReadOnlySpan<char>)) { }

            public TimeSpanToken(TTT type, int number, int leadingZeroes, ReadOnlySpan<char> separator)
            {
                ttt = type;
                num = number;
                zeroes = leadingZeroes;
                sep = separator;
            }

            public bool IsInvalidFraction()
            {
                Debug.Assert(ttt == TTT.Num);
                Debug.Assert(num > -1);

                if (num > maxFraction || zeroes > maxFractionDigits)
                    return true;

                if (num == 0 || zeroes == 0)
                    return false;

                // num > 0 && zeroes > 0 && num <= maxValue && zeroes <= maxPrecision
                return num >= maxFraction / Pow10(zeroes - 1);
            }
        }

        //
        //  TimeSpanTokenizer
        //
        //  Actions: TimeSpanTokenizer.GetNextToken() returns the next token in the input string.
        // 
        [IsByRefLike]
        private struct TimeSpanTokenizer
        {
            private ReadOnlySpan<char> m_value;
            private int m_pos;

            internal TimeSpanTokenizer(ReadOnlySpan<char> input) : this(input, 0) { }

            internal TimeSpanTokenizer(ReadOnlySpan<char> input, int startPosition)
            {
                m_value = input;
                m_pos = startPosition;
            }

            // used by the parsing routines that operate on standard-formats
            internal TimeSpanToken GetNextToken()
            {
                // Get the position of the next character to be processed.  If there is no
                // next character, we're at the end.
                int pos = m_pos;
                Debug.Assert(pos > -1);
                if (pos >= m_value.Length)
                {
                    return new TimeSpanToken(TTT.End);
                }

                // Now retrieve that character. If it's a digit, we're processing a number.
                int num = m_value[pos] - '0';
                if ((uint)num <= 9)
                {
                    int zeroes = 0;
                    if (num == 0)
                    {
                        // Read all leading zeroes.
                        zeroes = 1;
                        while (true)
                        {
                            int digit;
                            if (++m_pos >= m_value.Length || (uint)(digit = m_value[m_pos] - '0') > 9)
                            {
                                return new TimeSpanToken(TTT.Num, 0, zeroes, default(ReadOnlySpan<char>));
                            }

                            if (digit == 0)
                            {
                                zeroes++;
                                continue;
                            }

                            num = digit;
                            break;
                        }
                    }

                    // Continue to read as long as we're reading digits.
                    while (++m_pos < m_value.Length)
                    {
                        int digit = m_value[m_pos] - '0';
                        if ((uint)digit > 9)
                        {
                            break;
                        }

                        num = num * 10 + digit;
                        if ((num & 0xF0000000) != 0)
                        {
                            return new TimeSpanToken(TTT.NumOverflow);
                        }
                    }

                    return new TimeSpanToken(TTT.Num, num, zeroes, default(ReadOnlySpan<char>));
                }

                // Otherwise, we're processing a separator, and we've already processed the first
                // character of it.  Continue processing characters as long as they're not digits.
                int length = 1;
                while (true)
                {
                    if (++m_pos >= m_value.Length || (uint)(m_value[m_pos] - '0') <= 9)
                    {
                        break;
                    }
                    length++;
                }

                // Return the separator.
                return new TimeSpanToken(TTT.Sep, 0, 0, m_value.Slice(pos, length));
            }

            internal Boolean EOL
            {
                get
                {
                    return m_pos >= (m_value.Length - 1);
                }
            }

            // BackOne, NextChar, CurrentChar - used by ParseExact (ParseByFormat) to operate
            // on custom-formats where exact character-by-character control is allowed
            internal void BackOne()
            {
                if (m_pos > 0) --m_pos;
            }

            internal char NextChar
            {
                get
                {
                    int pos = ++m_pos;
                    return (uint)pos < (uint)m_value.Length ?
                        m_value[pos] :
                        (char)0;
                }
            }
        }

        // This stores intermediary parsing state for the standard formats
        [IsByRefLike]
        private struct TimeSpanRawInfo
        {
            internal TimeSpanFormat.FormatLiterals PositiveInvariant
            {
                get
                {
                    return TimeSpanFormat.PositiveInvariantFormatLiterals;
                }
            }
            internal TimeSpanFormat.FormatLiterals NegativeInvariant
            {
                get
                {
                    return TimeSpanFormat.NegativeInvariantFormatLiterals;
                }
            }

            internal TimeSpanFormat.FormatLiterals PositiveLocalized
            {
                get
                {
                    if (!m_posLocInit)
                    {
                        m_posLoc = new TimeSpanFormat.FormatLiterals();
                        m_posLoc.Init(m_fullPosPattern, false);
                        m_posLocInit = true;
                    }
                    return m_posLoc;
                }
            }
            internal TimeSpanFormat.FormatLiterals NegativeLocalized
            {
                get
                {
                    if (!m_negLocInit)
                    {
                        m_negLoc = new TimeSpanFormat.FormatLiterals();
                        m_negLoc.Init(m_fullNegPattern, false);
                        m_negLocInit = true;
                    }
                    return m_negLoc;
                }
            }

            internal Boolean FullAppCompatMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 5
                    && NumCount == 4
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.DayHourSep)
                    && StringSpanHelpers.Equals(literals2, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals3, pattern.AppCompatLiteral)
                    && StringSpanHelpers.Equals(literals4, pattern.End);
            }

            internal Boolean PartialAppCompatMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 4
                    && NumCount == 3
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals2, pattern.AppCompatLiteral)
                    && StringSpanHelpers.Equals(literals3, pattern.End);
            }
            // DHMSF (all values matched)
            internal Boolean FullMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == MaxLiteralTokens
                    && NumCount == MaxNumericTokens
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.DayHourSep)
                    && StringSpanHelpers.Equals(literals2, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals3, pattern.MinuteSecondSep)
                    && StringSpanHelpers.Equals(literals4, pattern.SecondFractionSep)
                    && StringSpanHelpers.Equals(literals5, pattern.End);
            }
            // D (no hours, minutes, seconds, or fractions)
            internal Boolean FullDMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 2
                    && NumCount == 1
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.End);
            }
            // HM (no days, seconds, or fractions)
            internal Boolean FullHMMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 3
                    && NumCount == 2
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals2, pattern.End);
            }
            // DHM (no seconds or fraction)
            internal Boolean FullDHMMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 4
                    && NumCount == 3
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.DayHourSep)
                    && StringSpanHelpers.Equals(literals2, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals3, pattern.End);
            }
            // HMS (no days or fraction)
            internal Boolean FullHMSMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 4
                    && NumCount == 3
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals2, pattern.MinuteSecondSep)
                    && StringSpanHelpers.Equals(literals3, pattern.End);
            }
            // DHMS (no fraction)
            internal Boolean FullDHMSMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 5
                    && NumCount == 4
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.DayHourSep)
                    && StringSpanHelpers.Equals(literals2, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals3, pattern.MinuteSecondSep)
                    && StringSpanHelpers.Equals(literals4, pattern.End);
            }
            // HMSF (no days)
            internal Boolean FullHMSFMatch(TimeSpanFormat.FormatLiterals pattern)
            {
                return SepCount == 5
                    && NumCount == 4
                    && StringSpanHelpers.Equals(literals0, pattern.Start)
                    && StringSpanHelpers.Equals(literals1, pattern.HourMinuteSep)
                    && StringSpanHelpers.Equals(literals2, pattern.MinuteSecondSep)
                    && StringSpanHelpers.Equals(literals3, pattern.SecondFractionSep)
                    && StringSpanHelpers.Equals(literals4, pattern.End);
            }

            internal TTT lastSeenTTT;
            internal int tokenCount;
            internal int SepCount;
            internal int NumCount;

            private TimeSpanFormat.FormatLiterals m_posLoc;
            private TimeSpanFormat.FormatLiterals m_negLoc;
            private Boolean m_posLocInit;
            private Boolean m_negLocInit;
            private String m_fullPosPattern;
            private String m_fullNegPattern;

            private const int MaxTokens = 11;
            private const int MaxLiteralTokens = 6;
            private const int MaxNumericTokens = 5;

            internal TimeSpanToken numbers0, numbers1, numbers2, numbers3, numbers4; // MaxNumbericTokens = 5
            internal ReadOnlySpan<char> literals0, literals1, literals2, literals3, literals4, literals5; // MaxLiteralTokens=6

            internal void Init(DateTimeFormatInfo dtfi)
            {
                Debug.Assert(dtfi != null);

                lastSeenTTT = TTT.None;
                tokenCount = 0;
                SepCount = 0;
                NumCount = 0;

                m_fullPosPattern = dtfi.FullTimeSpanPositivePattern;
                m_fullNegPattern = dtfi.FullTimeSpanNegativePattern;
                m_posLocInit = false;
                m_negLocInit = false;
            }

            internal Boolean ProcessToken(ref TimeSpanToken tok, ref TimeSpanResult result)
            {
                switch (tok.ttt)
                {
                    case TTT.Num:
                        if (tokenCount == 0)
                        {
                            if (!AddSep(default(ReadOnlySpan<char>), ref result)) return false;
                        }
                        if (!AddNum(tok, ref result)) return false;
                        break;
                    case TTT.Sep:
                        if (!AddSep(tok.sep, ref result)) return false;
                        break;
                    case TTT.NumOverflow:
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge", null);
                        return false;
                    default:
                        // Some unknown token or a repeat token type in the input
                        result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan", null);
                        return false;
                }

                lastSeenTTT = tok.ttt;
                Debug.Assert(tokenCount == (SepCount + NumCount), "tokenCount == (SepCount + NumCount)");
                return true;
            }

            private bool AddSep(ReadOnlySpan<char> sep, ref TimeSpanResult result)
            {
                if (SepCount >= MaxLiteralTokens || tokenCount >= MaxTokens)
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan", null);
                    return false;
                }

                switch (SepCount++)
                {
                    case 0: literals0 = sep; break;
                    case 1: literals1 = sep; break;
                    case 2: literals2 = sep; break;
                    case 3: literals3 = sep; break;
                    case 4: literals4 = sep; break;
                    default: literals5 = sep; break;
                }

                tokenCount++;
                return true;
            }
            private bool AddNum(TimeSpanToken num, ref TimeSpanResult result)
            {
                if (NumCount >= MaxNumericTokens || tokenCount >= MaxTokens)
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan", null);
                    return false;
                }

                switch (NumCount++)
                {
                    case 0: numbers0 = num; break;
                    case 1: numbers1 = num; break;
                    case 2: numbers2 = num; break;
                    case 3: numbers3 = num; break;
                    default: numbers4 = num; break;
                }

                tokenCount++;
                return true;
            }
        }

        // This will store the result of the parsing.  And it will eventually be used to construct a TimeSpan instance.
        private struct TimeSpanResult
        {
            internal TimeSpan parsedTimeSpan;
            internal readonly bool throwOnFailure;

            internal TimeSpanResult(bool canThrow) : this()
            {
                throwOnFailure = canThrow;
            }

            internal void SetFailure(ParseFailureKind failure, string failureMessageID)
            {
                SetFailure(failure, failureMessageID, null, null);
            }

            internal void SetFailure(ParseFailureKind failure, string failureMessageID, object failureMessageFormatArgument)
            {
                SetFailure(failure, failureMessageID, failureMessageFormatArgument, null);
            }

            internal void SetFailure(ParseFailureKind failure, string failureMessageID, object failureMessageFormatArgument, string failureArgumentName)
            {
                if (throwOnFailure)
                {
                    switch (failure)
                    {
                        case ParseFailureKind.ArgumentNull:
                            throw new ArgumentNullException(failureArgumentName, SR.GetResourceString(failureMessageID));

                        case ParseFailureKind.FormatWithParameter:
                            throw new FormatException(SR.Format(SR.GetResourceString(failureMessageID), failureMessageFormatArgument));

                        case ParseFailureKind.Overflow:
                            throw new OverflowException(SR.GetResourceString(failureMessageID));

                        default:
                            Debug.Assert(failure == ParseFailureKind.Format, $"Unexpected failure {failure}");
                            throw new FormatException(SR.GetResourceString(failureMessageID));
                    }
                }
            }
        }

        private static long Pow10(int pow)
        {
            switch (pow)
            {
                case 0:  return 1;
                case 1:  return 10;
                case 2:  return 100;
                case 3:  return 1000;
                case 4:  return 10000;
                case 5:  return 100000;
                case 6:  return 1000000;
                case 7:  return 10000000;
                default: return (long)Math.Pow(10, pow);
            }
        }

        private static bool TryTimeToTicks(bool positive, TimeSpanToken days, TimeSpanToken hours, TimeSpanToken minutes, TimeSpanToken seconds, TimeSpanToken fraction, out long result)
        {
            if (days.num > maxDays ||
                hours.num > maxHours ||
                minutes.num > maxMinutes ||
                seconds.num > maxSeconds ||
                fraction.IsInvalidFraction())
            {
                result = 0;
                return false;
            }

            Int64 ticks = ((Int64)days.num * 3600 * 24 + (Int64)hours.num * 3600 + (Int64)minutes.num * 60 + seconds.num) * 1000;
            if (ticks > TimeSpan.MaxMilliSeconds || ticks < TimeSpan.MinMilliSeconds)
            {
                result = 0;
                return false;
            }

            // Normalize the fraction component
            //
            // string representation => (zeroes,num) => resultant fraction ticks
            // ---------------------    ------------    ------------------------
            // ".9999999"            => (0,9999999)  => 9,999,999 ticks (same as constant maxFraction)
            // ".1"                  => (0,1)        => 1,000,000 ticks
            // ".01"                 => (1,1)        =>   100,000 ticks
            // ".001"                => (2,1)        =>    10,000 ticks
            long f = fraction.num;
            if (f != 0)
            {
                long lowerLimit = TimeSpan.TicksPerTenthSecond;
                if (fraction.zeroes > 0)
                {
                    long divisor = Pow10(fraction.zeroes);
                    lowerLimit = lowerLimit / divisor;
                }
                while (f < lowerLimit)
                {
                    f *= 10;
                }
            }
            result = ((long)ticks * TimeSpan.TicksPerMillisecond) + f;
            if (positive && result < 0)
            {
                result = 0;
                return false;
            }
            return true;
        }
        #endregion


        // ---- SECTION:  internal static methods called by System.TimeSpan ---------*
        //
        //  [Try]Parse, [Try]ParseExact, and [Try]ParseExactMultiple
        //
        //  Actions: Main methods called from TimeSpan.Parse
        #region ParseMethods
        internal static TimeSpan Parse(ReadOnlySpan<char> input, IFormatProvider formatProvider)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: true);
            bool success = TryParseTimeSpan(input, TimeSpanStandardStyles.Any, formatProvider, ref parseResult);
            Debug.Assert(success, "Should have thrown on failure");
            return parseResult.parsedTimeSpan;
        }
        internal static Boolean TryParse(ReadOnlySpan<char> input, IFormatProvider formatProvider, out TimeSpan result)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: false);

            if (TryParseTimeSpan(input, TimeSpanStandardStyles.Any, formatProvider, ref parseResult))
            {
                result = parseResult.parsedTimeSpan;
                return true;
            }

            result = default(TimeSpan);
            return false;
        }
        internal static TimeSpan ParseExact(ReadOnlySpan<char> input, String format, IFormatProvider formatProvider, TimeSpanStyles styles)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: true);
            bool success = TryParseExactTimeSpan(input, format, formatProvider, styles, ref parseResult);
            Debug.Assert(success, "Should have thrown on failure");
            return parseResult.parsedTimeSpan;
        }
        internal static Boolean TryParseExact(ReadOnlySpan<char> input, String format, IFormatProvider formatProvider, TimeSpanStyles styles, out TimeSpan result)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: false);

            if (TryParseExactTimeSpan(input, format, formatProvider, styles, ref parseResult))
            {
                result = parseResult.parsedTimeSpan;
                return true;
            }

            result = default(TimeSpan);
            return false;
        }
        internal static TimeSpan ParseExactMultiple(ReadOnlySpan<char> input, String[] formats, IFormatProvider formatProvider, TimeSpanStyles styles)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: true);
            bool success = TryParseExactMultipleTimeSpan(input, formats, formatProvider, styles, ref parseResult);
            Debug.Assert(success, "Should have thrown on failure");
            return parseResult.parsedTimeSpan;
        }
        internal static Boolean TryParseExactMultiple(ReadOnlySpan<char> input, String[] formats, IFormatProvider formatProvider, TimeSpanStyles styles, out TimeSpan result)
        {
            TimeSpanResult parseResult = new TimeSpanResult(canThrow: false);

            if (TryParseExactMultipleTimeSpan(input, formats, formatProvider, styles, ref parseResult))
            {
                result = parseResult.parsedTimeSpan;
                return true;
            }

            result = default(TimeSpan);
            return false;
        }
        #endregion


        // ---- SECTION:  private static methods that do the actual work ---------*
        #region TryParseTimeSpan
        //
        //  TryParseTimeSpan
        //
        //  Actions: Common private Parse method called by both Parse and TryParse
        // 
        private static Boolean TryParseTimeSpan(ReadOnlySpan<char> input, TimeSpanStandardStyles style, IFormatProvider formatProvider, ref TimeSpanResult result)
        {
            input = input.Trim();
            if (input.IsEmpty)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }

            TimeSpanTokenizer tokenizer = new TimeSpanTokenizer(input);

            TimeSpanRawInfo raw = new TimeSpanRawInfo();
            raw.Init(DateTimeFormatInfo.GetInstance(formatProvider));

            TimeSpanToken tok = tokenizer.GetNextToken();

            /* The following loop will break out when we reach the end of the str or
             * when we can determine that the input is invalid. */
            while (tok.ttt != TTT.End)
            {
                if (!raw.ProcessToken(ref tok, ref result))
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
                }
                tok = tokenizer.GetNextToken();
            }
            Debug.Assert(tokenizer.EOL);

            if (!ProcessTerminalState(ref raw, style, ref result))
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            return true;
        }



        //
        //  ProcessTerminalState
        //
        //  Actions: Validate the terminal state of a standard format parse.
        //           Sets result.parsedTimeSpan on success.
        // 
        // Calculates the resultant TimeSpan from the TimeSpanRawInfo
        //
        // try => +InvariantPattern, -InvariantPattern, +LocalizedPattern, -LocalizedPattern
        // 1) Verify Start matches
        // 2) Verify End matches
        // 3) 1 number  => d
        //    2 numbers => h:m
        //    3 numbers => h:m:s | d.h:m | h:m:.f
        //    4 numbers => h:m:s.f | d.h:m:s | d.h:m:.f
        //    5 numbers => d.h:m:s.f
        private static Boolean ProcessTerminalState(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.lastSeenTTT == TTT.Num)
            {
                TimeSpanToken tok = new TimeSpanToken();
                tok.ttt = TTT.Sep;
                if (!raw.ProcessToken(ref tok, ref result))
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
                }
            }

            switch (raw.NumCount)
            {
                case 1:
                    return ProcessTerminal_D(ref raw, style, ref result);
                case 2:
                    return ProcessTerminal_HM(ref raw, style, ref result);
                case 3:
                    return ProcessTerminal_HM_S_D(ref raw, style, ref result);
                case 4:
                    return ProcessTerminal_HMS_F_D(ref raw, style, ref result);
                case 5:
                    return ProcessTerminal_DHMSF(ref raw, style, ref result);
                default:
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
            }
        }

        //
        //  ProcessTerminal_DHMSF
        //
        //  Actions: Validate the 5-number "Days.Hours:Minutes:Seconds.Fraction" terminal case.
        //           Sets result.parsedTimeSpan on success.
        // 
        private static Boolean ProcessTerminal_DHMSF(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.SepCount != 6)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            Debug.Assert(raw.NumCount == 5);

            bool inv = ((style & TimeSpanStandardStyles.Invariant) != 0);
            bool loc = ((style & TimeSpanStandardStyles.Localized) != 0);

            bool positive = false;
            bool match = false;

            if (inv)
            {
                if (raw.FullMatch(raw.PositiveInvariant))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullMatch(raw.NegativeInvariant))
                {
                    match = true;
                    positive = false;
                }
            }
            if (loc)
            {
                if (!match && raw.FullMatch(raw.PositiveLocalized))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullMatch(raw.NegativeLocalized))
                {
                    match = true;
                    positive = false;
                }
            }
            long ticks;
            if (match)
            {
                if (!TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, raw.numbers4, out ticks))
                {
                    result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                    return false;
                }
                if (!positive)
                {
                    ticks = -ticks;
                    if (ticks > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }

            result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
            return false;
        }

        //
        //  ProcessTerminal_HMS_F_D
        //
        //  Actions: Validate the ambiguous 4-number "Hours:Minutes:Seconds.Fraction", "Days.Hours:Minutes:Seconds", or "Days.Hours:Minutes:.Fraction" terminal case.
        //           Sets result.parsedTimeSpan on success.
        // 
        private static Boolean ProcessTerminal_HMS_F_D(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.SepCount != 5 || (style & TimeSpanStandardStyles.RequireFull) != 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            Debug.Assert(raw.NumCount == 4);

            bool inv = ((style & TimeSpanStandardStyles.Invariant) != 0);
            bool loc = ((style & TimeSpanStandardStyles.Localized) != 0);

            long ticks = 0;
            bool positive = false;
            bool match = false;
            bool overflow = false;
            var zero = new TimeSpanToken(0);

            if (inv)
            {
                if (raw.FullHMSFMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMSMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullAppCompatMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullHMSFMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMSMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullAppCompatMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
            }
            if (loc)
            {
                if (!match && raw.FullHMSFMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMSMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullAppCompatMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullHMSFMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMSMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, raw.numbers3, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullAppCompatMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, raw.numbers3, out ticks);
                    overflow = overflow || !match;
                }
            }

            if (match)
            {
                if (!positive)
                {
                    ticks = -ticks;
                    if (ticks > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }

            if (overflow)
            {
                // we found at least one literal pattern match but the numbers just didn't fit
                result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                return false;
            }
            else
            {
                // we couldn't find a thing
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
        }

        //
        //  ProcessTerminal_HM_S_D
        //
        //  Actions: Validate the ambiguous 3-number "Hours:Minutes:Seconds", "Days.Hours:Minutes", or "Hours:Minutes:.Fraction" terminal case
        // 
        private static Boolean ProcessTerminal_HM_S_D(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.SepCount != 4 || (style & TimeSpanStandardStyles.RequireFull) != 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            Debug.Assert(raw.NumCount == 3);

            bool inv = ((style & TimeSpanStandardStyles.Invariant) != 0);
            bool loc = ((style & TimeSpanStandardStyles.Localized) != 0);

            bool positive = false;
            bool match = false;
            bool overflow = false;
            var zero = new TimeSpanToken(0);

            long ticks = 0;

            if (inv)
            {
                if (raw.FullHMSMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.PartialAppCompatMatch(raw.PositiveInvariant))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, zero, raw.numbers2, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullHMSMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.PartialAppCompatMatch(raw.NegativeInvariant))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, zero, raw.numbers2, out ticks);
                    overflow = overflow || !match;
                }
            }
            if (loc)
            {
                if (!match && raw.FullHMSMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.PartialAppCompatMatch(raw.PositiveLocalized))
                {
                    positive = true;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, zero, raw.numbers2, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullHMSMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, raw.numbers2, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.FullDHMMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, raw.numbers0, raw.numbers1, raw.numbers2, zero, zero, out ticks);
                    overflow = overflow || !match;
                }
                if (!match && raw.PartialAppCompatMatch(raw.NegativeLocalized))
                {
                    positive = false;
                    match = TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, zero, raw.numbers2, out ticks);
                    overflow = overflow || !match;
                }
            }

            if (match)
            {
                if (!positive)
                {
                    ticks = -ticks;
                    if (ticks > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }

            if (overflow)
            {
                // we found at least one literal pattern match but the numbers just didn't fit
                result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                return false;
            }
            else
            {
                // we couldn't find a thing
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
        }

        //
        //  ProcessTerminal_HM
        //
        //  Actions: Validate the 2-number "Hours:Minutes" terminal case
        // 
        private static Boolean ProcessTerminal_HM(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.SepCount != 3 || (style & TimeSpanStandardStyles.RequireFull) != 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            Debug.Assert(raw.NumCount == 2);

            bool inv = ((style & TimeSpanStandardStyles.Invariant) != 0);
            bool loc = ((style & TimeSpanStandardStyles.Localized) != 0);

            bool positive = false;
            bool match = false;

            if (inv)
            {
                if (raw.FullHMMatch(raw.PositiveInvariant))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullHMMatch(raw.NegativeInvariant))
                {
                    match = true;
                    positive = false;
                }
            }
            if (loc)
            {
                if (!match && raw.FullHMMatch(raw.PositiveLocalized))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullHMMatch(raw.NegativeLocalized))
                {
                    match = true;
                    positive = false;
                }
            }

            long ticks = 0;
            if (match)
            {
                var zero = new TimeSpanToken(0);
                if (!TryTimeToTicks(positive, zero, raw.numbers0, raw.numbers1, zero, zero, out ticks))
                {
                    result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                    return false;
                }
                if (!positive)
                {
                    ticks = -ticks;
                    if (ticks > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }

            result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
            return false;
        }


        //
        //  ProcessTerminal_D
        //
        //  Actions: Validate the 1-number "Days" terminal case
        // 
        private static Boolean ProcessTerminal_D(ref TimeSpanRawInfo raw, TimeSpanStandardStyles style, ref TimeSpanResult result)
        {
            if (raw.SepCount != 2 || (style & TimeSpanStandardStyles.RequireFull) != 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }
            Debug.Assert(raw.NumCount == 1);

            bool inv = ((style & TimeSpanStandardStyles.Invariant) != 0);
            bool loc = ((style & TimeSpanStandardStyles.Localized) != 0);

            bool positive = false;
            bool match = false;

            if (inv)
            {
                if (raw.FullDMatch(raw.PositiveInvariant))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullDMatch(raw.NegativeInvariant))
                {
                    match = true;
                    positive = false;
                }
            }
            if (loc)
            {
                if (!match && raw.FullDMatch(raw.PositiveLocalized))
                {
                    match = true;
                    positive = true;
                }
                if (!match && raw.FullDMatch(raw.NegativeLocalized))
                {
                    match = true;
                    positive = false;
                }
            }

            long ticks = 0;
            if (match)
            {
                var zero = new TimeSpanToken(0);
                if (!TryTimeToTicks(positive, raw.numbers0, zero, zero, zero, zero, out ticks))
                {
                    result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                    return false;
                }
                if (!positive)
                {
                    ticks = -ticks;
                    if (ticks > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }

            result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
            return false;
        }
        #endregion

        #region TryParseExactTimeSpan
        //
        //  TryParseExactTimeSpan
        //
        //  Actions: Common private ParseExact method called by both ParseExact and TryParseExact
        // 
        private static Boolean TryParseExactTimeSpan(ReadOnlySpan<char> input, String format, IFormatProvider formatProvider, TimeSpanStyles styles, ref TimeSpanResult result)
        {
            if (format == null)
            {
                result.SetFailure(ParseFailureKind.ArgumentNull, "ArgumentNull_String", null, nameof(format));
                return false;
            }

            if (format.Length == 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadFormatSpecifier");
                return false;
            }

            if (format.Length == 1)
            {
                switch (format[0])
                {
                    case 'c':
                    case 't':
                    case 'T':
                        return TryParseTimeSpanConstant(input, ref result); // fast path for legacy style TimeSpan formats.

                    case 'g':
                        return TryParseTimeSpan(input, TimeSpanStandardStyles.Localized, formatProvider, ref result);

                    case 'G':
                        return TryParseTimeSpan(input, TimeSpanStandardStyles.Localized | TimeSpanStandardStyles.RequireFull, formatProvider, ref result);

                    default:
                        result.SetFailure(ParseFailureKind.Format, "Format_BadFormatSpecifier");
                        return false;
                }
            }

            return TryParseByFormat(input, format, styles, ref result);
        }

        //
        //  TryParseByFormat
        //
        //  Actions: Parse the TimeSpan instance using the specified format.  Used by TryParseExactTimeSpan.
        // 
        private static Boolean TryParseByFormat(ReadOnlySpan<char> input, String format, TimeSpanStyles styles, ref TimeSpanResult result)
        {
            Debug.Assert(format != null, "format != null");

            bool seenDD = false;      // already processed days?
            bool seenHH = false;      // already processed hours?
            bool seenMM = false;      // already processed minutes?
            bool seenSS = false;      // already processed seconds?
            bool seenFF = false;      // already processed fraction?
            int dd = 0;               // parsed days
            int hh = 0;               // parsed hours
            int mm = 0;               // parsed minutes
            int ss = 0;               // parsed seconds
            int leadingZeroes = 0;    // number of leading zeroes in the parsed fraction
            int ff = 0;               // parsed fraction
            int i = 0;                // format string position
            int tokenLen = 0;         // length of current format token, used to update index 'i'

            TimeSpanTokenizer tokenizer = new TimeSpanTokenizer(input, -1);

            while (i < format.Length)
            {
                char ch = format[i];
                int nextFormatChar;
                switch (ch)
                {
                    case 'h':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        if (tokenLen > 2 || seenHH || !ParseExactDigits(ref tokenizer, tokenLen, out hh))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        seenHH = true;
                        break;
                    case 'm':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        if (tokenLen > 2 || seenMM || !ParseExactDigits(ref tokenizer, tokenLen, out mm))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        seenMM = true;
                        break;
                    case 's':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        if (tokenLen > 2 || seenSS || !ParseExactDigits(ref tokenizer, tokenLen, out ss))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        seenSS = true;
                        break;
                    case 'f':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        if (tokenLen > DateTimeFormat.MaxSecondsFractionDigits || seenFF || !ParseExactDigits(ref tokenizer, tokenLen, tokenLen, out leadingZeroes, out ff))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        seenFF = true;
                        break;
                    case 'F':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        if (tokenLen > DateTimeFormat.MaxSecondsFractionDigits || seenFF)
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        ParseExactDigits(ref tokenizer, tokenLen, tokenLen, out leadingZeroes, out ff);
                        seenFF = true;
                        break;
                    case 'd':
                        tokenLen = DateTimeFormat.ParseRepeatPattern(format, i, ch);
                        int tmp = 0;
                        if (tokenLen > 8 || seenDD || !ParseExactDigits(ref tokenizer, (tokenLen < 2) ? 1 : tokenLen, (tokenLen < 2) ? 8 : tokenLen, out tmp, out dd))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        seenDD = true;
                        break;
                    case '\'':
                    case '\"':
                        StringBuilder enquotedString = new StringBuilder();
                        if (!DateTimeParse.TryParseQuoteString(format, i, enquotedString, out tokenLen))
                        {
                            result.SetFailure(ParseFailureKind.FormatWithParameter, "Format_BadQuote", ch);
                            return false;
                        }
                        if (!ParseExactLiteral(ref tokenizer, enquotedString))
                        {
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        break;
                    case '%':
                        // Optional format character.
                        // For example, format string "%d" will print day 
                        // Most of the cases, "%" can be ignored.
                        nextFormatChar = DateTimeFormat.ParseNextChar(format, i);
                        // nextFormatChar will be -1 if we already reach the end of the format string.
                        // Besides, we will not allow "%%" appear in the pattern.
                        if (nextFormatChar >= 0 && nextFormatChar != (int)'%')
                        {
                            tokenLen = 1; // skip the '%' and process the format character
                            break;
                        }
                        else
                        {
                            // This means that '%' is at the end of the format string or
                            // "%%" appears in the format string.
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                    case '\\':
                        // Escaped character.  Can be used to insert character into the format string.
                        // For example, "\d" will insert the character 'd' into the string.
                        //
                        nextFormatChar = DateTimeFormat.ParseNextChar(format, i);
                        if (nextFormatChar >= 0 && tokenizer.NextChar == (char)nextFormatChar)
                        {
                            tokenLen = 2;
                        }
                        else
                        {
                            // This means that '\' is at the end of the format string or the literal match failed.
                            result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                            return false;
                        }
                        break;
                    default:
                        result.SetFailure(ParseFailureKind.Format, "Format_InvalidString");
                        return false;
                }
                i += tokenLen;
            }


            if (!tokenizer.EOL)
            {
                // the custom format didn't consume the entire input
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }

            long ticks = 0;
            bool positive = (styles & TimeSpanStyles.AssumeNegative) == 0;
            if (TryTimeToTicks(positive, new TimeSpanToken(dd),
                                         new TimeSpanToken(hh),
                                         new TimeSpanToken(mm),
                                         new TimeSpanToken(ss),
                                         new TimeSpanToken(ff, leadingZeroes),
                                         out ticks))
            {
                if (!positive) ticks = -ticks;
                result.parsedTimeSpan._ticks = ticks;
                return true;
            }
            else
            {
                result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                return false;
            }
        }

        private static Boolean ParseExactDigits(ref TimeSpanTokenizer tokenizer, int minDigitLength, out int result)
        {
            result = 0;
            int zeroes = 0;
            int maxDigitLength = (minDigitLength == 1) ? 2 : minDigitLength;
            return ParseExactDigits(ref tokenizer, minDigitLength, maxDigitLength, out zeroes, out result);
        }
        private static Boolean ParseExactDigits(ref TimeSpanTokenizer tokenizer, int minDigitLength, int maxDigitLength, out int zeroes, out int result)
        {
            result = 0;
            zeroes = 0;

            int tokenLength = 0;
            while (tokenLength < maxDigitLength)
            {
                char ch = tokenizer.NextChar;
                if (ch < '0' || ch > '9')
                {
                    tokenizer.BackOne();
                    break;
                }
                result = result * 10 + (ch - '0');
                if (result == 0) zeroes++;
                tokenLength++;
            }
            return (tokenLength >= minDigitLength);
        }
        private static Boolean ParseExactLiteral(ref TimeSpanTokenizer tokenizer, StringBuilder enquotedString)
        {
            for (int i = 0; i < enquotedString.Length; i++)
            {
                if (enquotedString[i] != tokenizer.NextChar)
                    return false;
            }
            return true;
        }
        #endregion

        #region TryParseTimeSpanConstant
        //
        // TryParseTimeSpanConstant
        //
        // Actions: Parses the "c" (constant) format.  This code is 100% identical to the non-globalized v1.0-v3.5 TimeSpan.Parse() routine
        //          and exists for performance/appcompat with legacy callers who cannot move onto the globalized Parse overloads.
        //
        private static Boolean TryParseTimeSpanConstant(ReadOnlySpan<char> input, ref TimeSpanResult result)
        {
            return (new StringParser().TryParse(input, ref result));
        }

        [IsByRefLike]
        private struct StringParser
        {
            private ReadOnlySpan<char> str;
            private char ch;
            private int pos;
            private int len;

            internal void NextChar()
            {
                if (pos < len) pos++;
                ch = pos < len ? str[pos] : (char)0;
            }

            internal char NextNonDigit()
            {
                int i = pos;
                while (i < len)
                {
                    char ch = str[i];
                    if (ch < '0' || ch > '9') return ch;
                    i++;
                }
                return (char)0;
            }

            internal bool TryParse(ReadOnlySpan<char> input, ref TimeSpanResult result)
            {
                result.parsedTimeSpan._ticks = 0;

                str = input;
                len = input.Length;
                pos = -1;
                NextChar();
                SkipBlanks();
                bool negative = false;
                if (ch == '-')
                {
                    negative = true;
                    NextChar();
                }
                long time;
                if (NextNonDigit() == ':')
                {
                    if (!ParseTime(out time, ref result))
                    {
                        return false;
                    };
                }
                else
                {
                    int days;
                    if (!ParseInt((int)(0x7FFFFFFFFFFFFFFFL / TimeSpan.TicksPerDay), out days, ref result))
                    {
                        return false;
                    }
                    time = days * TimeSpan.TicksPerDay;
                    if (ch == '.')
                    {
                        NextChar();
                        long remainingTime;
                        if (!ParseTime(out remainingTime, ref result))
                        {
                            return false;
                        };
                        time += remainingTime;
                    }
                }
                if (negative)
                {
                    time = -time;
                    // Allow -0 as well
                    if (time > 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                else
                {
                    if (time < 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                }
                SkipBlanks();
                if (pos < len)
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
                }
                result.parsedTimeSpan._ticks = time;
                return true;
            }

            internal bool ParseInt(int max, out int i, ref TimeSpanResult result)
            {
                i = 0;
                int p = pos;
                while (ch >= '0' && ch <= '9')
                {
                    if ((i & 0xF0000000) != 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                    i = i * 10 + ch - '0';
                    if (i < 0)
                    {
                        result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                        return false;
                    }
                    NextChar();
                }
                if (p == pos)
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
                }
                if (i > max)
                {
                    result.SetFailure(ParseFailureKind.Overflow, "Overflow_TimeSpanElementTooLarge");
                    return false;
                }
                return true;
            }

            internal bool ParseTime(out long time, ref TimeSpanResult result)
            {
                time = 0;
                int unit;
                if (!ParseInt(23, out unit, ref result))
                {
                    return false;
                }
                time = unit * TimeSpan.TicksPerHour;
                if (ch != ':')
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                    return false;
                }
                NextChar();
                if (!ParseInt(59, out unit, ref result))
                {
                    return false;
                }
                time += unit * TimeSpan.TicksPerMinute;
                if (ch == ':')
                {
                    NextChar();
                    // allow seconds with the leading zero
                    if (ch != '.')
                    {
                        if (!ParseInt(59, out unit, ref result))
                        {
                            return false;
                        }
                        time += unit * TimeSpan.TicksPerSecond;
                    }
                    if (ch == '.')
                    {
                        NextChar();
                        int f = (int)TimeSpan.TicksPerSecond;
                        while (f > 1 && ch >= '0' && ch <= '9')
                        {
                            f /= 10;
                            time += (ch - '0') * f;
                            NextChar();
                        }
                    }
                }
                return true;
            }

            internal void SkipBlanks()
            {
                while (ch == ' ' || ch == '\t') NextChar();
            }
        }
        #endregion

        #region TryParseExactMultipleTimeSpan
        //
        //  TryParseExactMultipleTimeSpan
        //
        //  Actions: Common private ParseExactMultiple method called by both ParseExactMultiple and TryParseExactMultiple
        // 
        private static Boolean TryParseExactMultipleTimeSpan(ReadOnlySpan<char> input, String[] formats, IFormatProvider formatProvider, TimeSpanStyles styles, ref TimeSpanResult result)
        {
            if (formats == null)
            {
                result.SetFailure(ParseFailureKind.ArgumentNull, "ArgumentNull_String", null, nameof(formats));
                return false;
            }

            if (input.Length == 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
                return false;
            }

            if (formats.Length == 0)
            {
                result.SetFailure(ParseFailureKind.Format, "Format_BadFormatSpecifier");
                return false;
            }

            //
            // Do a loop through the provided formats and see if we can parse succesfully in
            // one of the formats.
            //
            for (int i = 0; i < formats.Length; i++)
            {
                if (formats[i] == null || formats[i].Length == 0)
                {
                    result.SetFailure(ParseFailureKind.Format, "Format_BadFormatSpecifier");
                    return false;
                }

                // Create a new non-throwing result each time to ensure the runs are independent.
                TimeSpanResult innerResult = new TimeSpanResult(canThrow: false);

                if (TryParseExactTimeSpan(input, formats[i], formatProvider, styles, ref innerResult))
                {
                    result.parsedTimeSpan = innerResult.parsedTimeSpan;
                    return true;
                }
            }

            result.SetFailure(ParseFailureKind.Format, "Format_BadTimeSpan");
            return (false);
        }
        #endregion
    }
}
