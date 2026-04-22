// Monaco editor syntax mode for Overt, used by the Compiler Explorer editor
// pane. Without this, Overt source renders as plain text in CE.
//
// Destination: compiler-explorer/compiler-explorer:static/modes/overt-mode.ts
//
// Keywords here MUST stay in sync with the TextMate grammar at
// vscode-extension/syntaxes/overt.tmLanguage.json. When either changes,
// update both in the same commit.

import * as monaco from 'monaco-editor';

export function definition(): monaco.languages.IMonarchLanguage {
    return {
        defaultToken: '',
        tokenPostfix: '.ov',

        keywords: [
            // Control flow
            'if', 'else', 'match', 'for', 'each', 'while', 'loop', 'return',
            'parallel', 'race', 'trace', 'with',
            // Declarations / storage
            'fn', 'let', 'mut', 'record', 'enum', 'type', 'module', 'use',
            'pub', 'extern', 'unsafe', 'where', 'binds', 'from',
        ],

        typeKeywords: [
            // Built-in effects; capital-letter effect variables are handled by
            // the identifier rule below.
            'io', 'async', 'inference', 'fails',
        ],

        constants: [
            'true', 'false', 'Ok', 'Err', 'Some', 'None',
        ],

        operators: [
            '=', '>', '<', '!', '~', '?', ':',
            '==', '<=', '>=', '!=', '&&', '||',
            '+', '-', '*', '/', '%', '&', '|', '^',
            '|>', '|>?',
        ],

        symbols: /[=><!~?:&|+\-*\/\^%]+/,

        tokenizer: {
            root: [
                // Identifiers and keywords
                [/[a-zA-Z_][\w]*/, {
                    cases: {
                        '@keywords': 'keyword',
                        '@typeKeywords': 'type',
                        '@constants': 'constant',
                        '@default': 'identifier',
                    },
                }],

                // Annotations: @derive, @pure, etc.
                [/@[a-zA-Z_][\w]*/, 'annotation'],

                // Whitespace and comments
                { include: '@whitespace' },

                // Numbers
                [/\b0x[0-9A-Fa-f_]+\b/, 'number.hex'],
                [/\b0b[01_]+\b/, 'number.binary'],
                [/\b\d[\d_]*\.\d[\d_]*([eE][+-]?\d+)?\b/, 'number.float'],
                [/\b\d[\d_]*\b/, 'number'],

                // Strings (interpolation is tokenized by the lexer; Monaco
                // just needs to know where strings start/end for coloring).
                [/"/, { token: 'string.quote', bracket: '@open', next: '@string' }],

                // Pipe operators (must come before generic symbol rule)
                [/\|>\?/, 'operator'],
                [/\|>/, 'operator'],

                // Generic operator matching
                [/@symbols/, {
                    cases: {
                        '@operators': 'operator',
                        '@default': '',
                    },
                }],

                // Delimiters
                [/[{}()\[\]]/, '@brackets'],
                [/[,;]/, 'delimiter'],
            ],

            whitespace: [
                [/\s+/, 'white'],
                [/\/\/.*$/, 'comment'],
                [/\/\*/, { token: 'comment', next: '@blockComment' }],
            ],

            blockComment: [
                [/[^\/*]+/, 'comment'],
                [/\*\//, { token: 'comment', next: '@pop' }],
                [/[\/*]/, 'comment'],
            ],

            string: [
                [/[^\\"$]+/, 'string'],
                [/\\./, 'string.escape'],
                [/\$[a-zA-Z_][\w]*/, 'variable'],
                [/"/, { token: 'string.quote', bracket: '@close', next: '@pop' }],
            ],
        },
    };
}
