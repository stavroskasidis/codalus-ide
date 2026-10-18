// The control set's shared runtime for the mockups in this folder (docs/design/controls.md).
// - THEMES: the seven palettes the mockups draw in: Classic Blue, Light, Dark and High Contrast (the values of
//   src/Codalus.Shell/Theming/Tokens.axaml) and Harbor Light, Dark and High contrast (docs/design/shell-themes.md).
//   Keys that are existing tokens carry their value; the others are the proposed control tokens, listed in TOKENS with
//   the token name each one becomes.
// - The icon sprite: 16 px drawings in the style of Fluent UI System Icons (regular), our own, drawn from scratch.
//   The app compiles the real ones from eng/icons; these only stand in for them. Colors come from the icon palette.
// Each mockup loads this file before ../shell-themes/support.js, and its component calls CodalusControls.theme(name).
(() => {
  'use strict';

  // Classic's syntax colors are the editor's light and dark defaults made AA on the code block's surface.
  const THEMES = {
    'Classic Blue': {
      family: 'Classic', kind: 'light', radius: 2,
      canvas: '#CCD5F0', bg: '#CCD5F0', fg: '#1E1E1E', strong: '#000000', muted: '#4D5A73', border: '#8E9BBC', softBorder: '#D5DAE8',
      panel: '#F5F5F5', hover: '#FDF4BF', rowHover: '#FEF7D9', hoverFg: '#1E1E1E', pressed: '#FFE8A6', sel: '#C6D0EA', selFg: '#1E1E1E', selInactive: '#DDE2EF', selInactiveFg: '#1E1E1E',
      accent: '#0E70C0', accentFg: '#FFFFFF', accentHover: '#0C62A8', accentPressed: '#0A5591', focus: '#0E70C0',
      input: '#FFFFFF', inputBorder: '#8E9BBC', inputBorderHover: '#5B6E99', placeholder: '#4D5A73',
      disabledFg: '#7D8496', disabledBg: '#E9ECF4', disabledBorder: '#C3CADB',
      overlay: '#FFFFFF', overlayBorder: '#8E9BBC', shadow: '0 8px 24px rgba(24,38,77,.28)', scrim: 'rgba(31,45,84,.45)',
      link: '#0054A0', match: '#0066BF', matchBg: '#FFE58F', matchFg: '#1E1E1E',
      errorFg: '#A1260D', warnFg: '#7A5400', okFg: '#116329', infoFg: '#0054A0', error: '#E51400', warning: '#9A6A00', info: '#1A85FF', ok: '#1E7F34',
      btn: '#FFFFFF', btnFg: '#1E1E1E', btnBorder: '#8E9BBC', btnHover: '#FDF4BF', btnHoverBorder: '#E5C365', btnPressed: '#FFE8A6', btnPressedBorder: '#E5C365',
      tbHover: '#FDF4BF', tbHoverBorder: '#E5C365', tbPressed: '#FFE8A6', tbChecked: '#FFEFBB', tbCheckedBorder: '#E5C365', tbCheckedFg: '#1E1E1E',
      seg: '#FFFFFF', segOn: '#0E70C0', segOnFg: '#FFFFFF',
      chkBorder: '#5B6E99', chkBg: '#FFFFFF', swOff: '#5B6E99',
      toolbar: '#D6DBE9', toolbarBorder: '#8E9BBC',
      capActive: '#FFF29D', capActiveFg: '#1E1E1E', capInactive: '#4D6082', capInactiveFg: '#FFFFFF',
      tabs: '#4D6082', tabsFg: '#FFFFFF', tabOn: '#F5F5F5', tabOnFg: '#1E1E1E', tabIndicator: '#F5CC84',
      header: '#EAEDF5', rule: '#E2E6F0',
      editor: '#FFFFFF', editorFg: '#1E1E1E', lineNo: '#6E7681', curLine: '#F0F0F0',
      chipOk: '#E3F2E6', chipOkFg: '#116329', chipErr: '#FBE7E3', chipErrFg: '#9A220B', chipWarn: '#FFF2CC', chipWarnFg: '#6E4B00', chipInfo: '#E1ECF8', chipInfoFg: '#0054A0',
      badge: '#DDE2EF', badgeFg: '#1E1E1E', dlgFooter: '#EEF0F6', userBubble: '#DCE6F6', userBubbleFg: '#1E1E1E', codeHeader: '#EEF0F6',
      titleBar: '#CCD5F0', titleBarFg: '#1E1E1E',
      icBlue: '#00539C', icGreen: '#388A34', icRed: '#A1260D', icOrange: '#B26B12', icPurple: '#652D90', icYellow: '#8A6400', icFolder: '#8A6320', icOutline: '#424242',
      kw: '#0000FF', ty: '#1E6E87', fn: '#74531F', st: '#A31515', nu: '#0F6E3E', cm: '#3B7A1E', pu: '#1E1E1E'
    },
    'Classic Light': {
      family: 'Classic', kind: 'light', radius: 2,
      canvas: '#EEEEF2', bg: '#EEEEF2', fg: '#1E1E1E', strong: '#000000', muted: '#646464', border: '#CCCEDB', softBorder: '#E2E3EA',
      panel: '#F5F5F5', hover: '#C9DEF5', rowHover: '#E8F1FB', hoverFg: '#1E1E1E', pressed: '#C3DBF4', sel: '#CCE8FF', selFg: '#1E1E1E', selInactive: '#E4E6EE', selInactiveFg: '#1E1E1E',
      accent: '#007ACC', accentFg: '#FFFFFF', accentHover: '#0068B0', accentPressed: '#005A9E', focus: '#007ACC',
      input: '#FFFFFF', inputBorder: '#BDBFCC', inputBorderHover: '#8D90A0', placeholder: '#646464',
      disabledFg: '#8A8A8A', disabledBg: '#EDEDEF', disabledBorder: '#D7D8DF',
      overlay: '#FFFFFF', overlayBorder: '#C8C8C8', shadow: '0 8px 24px rgba(0,0,0,.18)', scrim: 'rgba(30,30,30,.38)',
      link: '#006AB1', match: '#0066BF', matchBg: '#FFE58F', matchFg: '#1E1E1E',
      errorFg: '#A1260D', warnFg: '#7A5400', okFg: '#116329', infoFg: '#0062A8', error: '#E51400', warning: '#9A6A00', info: '#1A85FF', ok: '#1E7F34',
      btn: '#FDFDFD', btnFg: '#1E1E1E', btnBorder: '#C2C4D0', btnHover: '#EAF3FC', btnHoverBorder: '#7EB4EA', btnPressed: '#CCE4F7', btnPressedBorder: '#569DE5',
      tbHover: '#DCEAF8', tbHoverBorder: '#B3D3F2', tbPressed: '#C3DBF4', tbChecked: '#D5E8FA', tbCheckedBorder: '#007ACC', tbCheckedFg: '#1E1E1E',
      seg: '#FDFDFD', segOn: '#007ACC', segOnFg: '#FFFFFF',
      chkBorder: '#6E7080', chkBg: '#FFFFFF', swOff: '#6E7080',
      toolbar: '#EEEEF2', toolbarBorder: '#CCCEDB',
      capActive: '#007ACC', capActiveFg: '#FFFFFF', capInactive: '#EEEEF2', capInactiveFg: '#3C3C3C',
      tabs: '#EEEEF2', tabsFg: '#1E1E1E', tabOn: '#F5F5F5', tabOnFg: '#0E5A9C', tabIndicator: '#007ACC',
      header: '#F0F0F3', rule: '#EBEBEF',
      editor: '#FFFFFF', editorFg: '#1E1E1E', lineNo: '#6E7681', curLine: '#F0F0F0',
      chipOk: '#E3F2E6', chipOkFg: '#116329', chipErr: '#FBE7E3', chipErrFg: '#9A220B', chipWarn: '#FFF2CC', chipWarnFg: '#6E4B00', chipInfo: '#E1EEF9', chipInfoFg: '#005A9E',
      badge: '#E4E6EE', badgeFg: '#1E1E1E', dlgFooter: '#F2F2F5', userBubble: '#E1EEF9', userBubbleFg: '#1E1E1E', codeHeader: '#F2F2F5',
      titleBar: '#EEEEF2', titleBarFg: '#1E1E1E',
      icBlue: '#00539C', icGreen: '#388A34', icRed: '#A1260D', icOrange: '#B26B12', icPurple: '#652D90', icYellow: '#8A6400', icFolder: '#8A6320', icOutline: '#424242',
      kw: '#0000FF', ty: '#1E6E87', fn: '#74531F', st: '#A31515', nu: '#0F6E3E', cm: '#3B7A1E', pu: '#1E1E1E'
    },
    'Classic Dark': {
      family: 'Classic', kind: 'dark', radius: 2,
      canvas: '#1F1F1F', bg: '#1F1F1F', fg: '#F1F1F1', strong: '#FFFFFF', muted: '#A6A6A6', border: '#3F3F46', softBorder: '#2F2F33',
      panel: '#252526', hover: '#3E3E40', rowHover: '#2A2D2E', hoverFg: '#F1F1F1', pressed: '#2A4A6B', sel: '#3F3F46', selFg: '#F1F1F1', selInactive: '#323236', selInactiveFg: '#F1F1F1',
      accent: '#3794FF', accentFg: '#000000', accentHover: '#5AA8FF', accentPressed: '#2B7FE0', focus: '#3794FF',
      input: '#2B2B2B', inputBorder: '#4A4A50', inputBorderHover: '#6A6A72', placeholder: '#A6A6A6',
      disabledFg: '#7A7A7A', disabledBg: '#2A2A2B', disabledBorder: '#38383C',
      overlay: '#252526', overlayBorder: '#454545', shadow: '0 8px 24px rgba(0,0,0,.55)', scrim: 'rgba(0,0,0,.55)',
      link: '#4DAAFC', match: '#2AAAFF', matchBg: '#653A0B', matchFg: '#FFFFFF',
      errorFg: '#F48771', warnFg: '#CCA700', okFg: '#73C991', infoFg: '#75BEFF', error: '#F14C4C', warning: '#CCA700', info: '#3794FF', ok: '#73C991',
      btn: '#333337', btnFg: '#F1F1F1', btnBorder: '#46464C', btnHover: '#3E3E42', btnHoverBorder: '#5C5C63', btnPressed: '#2A4A6B', btnPressedBorder: '#3794FF',
      tbHover: '#3E3E40', tbHoverBorder: '#55555B', tbPressed: '#2A4A6B', tbChecked: '#1E3D5C', tbCheckedBorder: '#3794FF', tbCheckedFg: '#F1F1F1',
      seg: '#2D2D30', segOn: '#3794FF', segOnFg: '#000000',
      chkBorder: '#9A9AA2', chkBg: '#2B2B2B', swOff: '#9A9AA2',
      toolbar: '#1F1F1F', toolbarBorder: '#3F3F46',
      capActive: '#007ACC', capActiveFg: '#FFFFFF', capInactive: '#252526', capInactiveFg: '#D0D0D0',
      tabs: '#1F1F1F', tabsFg: '#D0D0D0', tabOn: '#252526', tabOnFg: '#4DAAFC', tabIndicator: '#3794FF',
      header: '#2A2A2C', rule: '#2E2E31',
      editor: '#1E1E1E', editorFg: '#DCDCDC', lineNo: '#2B91AF', curLine: '#2E2E2E',
      chipOk: '#1F3A28', chipOkFg: '#8FDBA8', chipErr: '#4A2420', chipErrFg: '#F8A08F', chipWarn: '#3D3412', chipWarnFg: '#E5C24A', chipInfo: '#1B3550', chipInfoFg: '#8CC8FF',
      badge: '#3F3F46', badgeFg: '#F1F1F1', dlgFooter: '#2A2A2C', userBubble: '#1E3A5C', userBubbleFg: '#F1F1F1', codeHeader: '#2A2A2C',
      titleBar: '#1F1F1F', titleBarFg: '#F1F1F1',
      icBlue: '#75BEFF', icGreen: '#89D185', icRed: '#F48771', icOrange: '#E8AB53', icPurple: '#B180D7', icYellow: '#FFCC00', icFolder: '#DCB67A', icOutline: '#C5C5C5',
      kw: '#569CD6', ty: '#4EC9B0', fn: '#DCDCAA', st: '#D69D85', nu: '#B5CEA8', cm: '#6A9955', pu: '#DCDCDC'
    },
    'Classic High Contrast': {
      family: 'Classic', kind: 'hc', radius: 2,
      canvas: '#000000', bg: '#000000', fg: '#FFFFFF', strong: '#FFFFFF', muted: '#FFFFFF', border: '#6FC3DF', softBorder: '#6FC3DF',
      panel: '#000000', hover: '#1F1F1F', rowHover: '#000000', hoverFg: '#FFFFFF', pressed: '#0E4A7B', sel: '#0E4A7B', selFg: '#FFFFFF', selInactive: '#1F1F1F', selInactiveFg: '#FFFFFF',
      accent: '#1AEBFF', accentFg: '#000000', accentHover: '#7CF3FF', accentPressed: '#1AEBFF', focus: '#F38518',
      input: '#000000', inputBorder: '#FFFFFF', inputBorderHover: '#F38518', placeholder: '#FFFFFF',
      disabledFg: '#A0A0A0', disabledBg: '#000000', disabledBorder: '#7A7A7A',
      overlay: '#000000', overlayBorder: '#6FC3DF', shadow: 'none', scrim: 'rgba(0,0,0,.7)',
      link: '#21A6FF', match: '#1AEBFF', matchBg: '#000000', matchFg: '#1AEBFF',
      errorFg: '#F48771', warnFg: '#FFD700', okFg: '#00FF00', infoFg: '#75BEFF', error: '#F48771', warning: '#FFD700', info: '#75BEFF', ok: '#00FF00',
      btn: '#000000', btnFg: '#FFFFFF', btnBorder: '#FFFFFF', btnHover: '#1F1F1F', btnHoverBorder: '#F38518', btnPressed: '#0E4A7B', btnPressedBorder: '#F38518',
      tbHover: '#1F1F1F', tbHoverBorder: '#F38518', tbPressed: '#0E4A7B', tbChecked: '#0E4A7B', tbCheckedBorder: '#1AEBFF', tbCheckedFg: '#FFFFFF',
      seg: '#000000', segOn: '#1AEBFF', segOnFg: '#000000',
      chkBorder: '#FFFFFF', chkBg: '#000000', swOff: '#FFFFFF',
      toolbar: '#000000', toolbarBorder: '#6FC3DF',
      capActive: '#1AEBFF', capActiveFg: '#000000', capInactive: '#000000', capInactiveFg: '#FFFFFF',
      tabs: '#000000', tabsFg: '#FFFFFF', tabOn: '#000000', tabOnFg: '#1AEBFF', tabIndicator: '#1AEBFF',
      header: '#000000', rule: '#3A3A3A',
      editor: '#000000', editorFg: '#FFFFFF', lineNo: '#FFFFFF', curLine: '#000000',
      chipOk: '#000000', chipOkFg: '#00FF00', chipErr: '#000000', chipErrFg: '#F48771', chipWarn: '#000000', chipWarnFg: '#FFD700', chipInfo: '#000000', chipInfoFg: '#75BEFF',
      badge: '#000000', badgeFg: '#FFFFFF', dlgFooter: '#000000', userBubble: '#000000', userBubbleFg: '#FFFFFF', codeHeader: '#000000',
      titleBar: '#000000', titleBarFg: '#FFFFFF',
      icBlue: '#FFFFFF', icGreen: '#FFFFFF', icRed: '#FFFFFF', icOrange: '#FFFFFF', icPurple: '#FFFFFF', icYellow: '#FFFFFF', icFolder: '#FFFFFF', icOutline: '#FFFFFF',
      kw: '#7CC7FF', ty: '#4FF0CF', fn: '#FFE66D', st: '#FFA680', nu: '#E8AAFF', cm: '#9FE870', pu: '#FFFFFF'
    },
    'Harbor Light': {
      family: 'Harbor', kind: 'light', radius: 3,
      canvas: '#D5DDEF', bg: '#D5DDEF', fg: '#1B2645', strong: '#0E1735', muted: '#4E5873', border: '#BCC7E0', softBorder: '#E3E8F3',
      panel: '#F4F6FC', hover: '#E0E6F6', rowHover: '#EBF0FA', hoverFg: '#1B2645', pressed: '#CFD9F1', sel: '#CCD9F6', selFg: '#0E1735', selInactive: '#E3E8F5', selInactiveFg: '#0E1735',
      accent: '#2B59C3', accentFg: '#FFFFFF', accentHover: '#244CA8', accentPressed: '#1E408F', focus: '#2B59C3',
      input: '#FFFFFF', inputBorder: '#AEBBD9', inputBorderHover: '#7F90BC', placeholder: '#4E5873',
      disabledFg: '#7F889F', disabledBg: '#EBEFF8', disabledBorder: '#D2DAEC',
      overlay: '#F4F6FC', overlayBorder: '#BCC7E0', shadow: '0 10px 28px rgba(27,38,69,.22)', scrim: 'rgba(27,38,69,.42)',
      link: '#1D49B0', match: '#1D49B0', matchBg: '#FFE7A3', matchFg: '#0E1735',
      errorFg: '#A1260D', warnFg: '#7A5400', okFg: '#116329', infoFg: '#1D49B0', error: '#E51400', warning: '#9A6A00', info: '#1A85FF', ok: '#1E7F34',
      btn: '#FFFFFF', btnFg: '#1B2645', btnBorder: '#AEBBD9', btnHover: '#EDF1FA', btnHoverBorder: '#8FA0C8', btnPressed: '#DCE4F6', btnPressedBorder: '#6F84B8',
      tbHover: '#E0E6F6', tbHoverBorder: '#C3CDE6', tbPressed: '#CFD9F1', tbChecked: '#D9E3F8', tbCheckedBorder: '#2B59C3', tbCheckedFg: '#0E1735',
      seg: '#FFFFFF', segOn: '#2B59C3', segOnFg: '#FFFFFF',
      chkBorder: '#5D6A8C', chkBg: '#FFFFFF', swOff: '#5D6A8C',
      toolbar: '#E3E9F6', toolbarBorder: '#BCC7E0',
      capActive: '#2B59C3', capActiveFg: '#FFFFFF', capInactive: '#F4F6FC', capInactiveFg: '#1B2645',
      tabs: '#DCE3F2', tabsFg: '#1B2645', tabOn: '#F4F6FC', tabOnFg: '#0E1735', tabIndicator: '#2B59C3',
      header: '#EEF1F9', rule: '#E8ECF6',
      editor: '#FFFFFF', editorFg: '#1B1F2A', lineNo: '#6A7490', curLine: '#F2F6FE',
      chipOk: '#E2F1E6', chipOkFg: '#116329', chipErr: '#FBE6E2', chipErrFg: '#9A220B', chipWarn: '#FDF0CF', chipWarnFg: '#6B4900', chipInfo: '#E3EAFA', chipInfoFg: '#1D49B0',
      badge: '#E1E7F5', badgeFg: '#1B2645', dlgFooter: '#EBEFF8', userBubble: '#E1E9FA', userBubbleFg: '#0E1735', codeHeader: '#EEF1F9',
      titleBar: '#1B2645', titleBarFg: '#E8ECF7',
      icBlue: '#00539C', icGreen: '#388A34', icRed: '#A1260D', icOrange: '#B26B12', icPurple: '#652D90', icYellow: '#8A6400', icFolder: '#8A6320', icOutline: '#6E6E6E',
      kw: '#1D3FBF', ty: '#0F7068', fn: '#74460A', st: '#A3361A', nu: '#6A3A9E', cm: '#3D6B2C', pu: '#1B1F2A'
    },
    'Harbor Dark': {
      family: 'Harbor', kind: 'dark', radius: 3,
      canvas: '#121827', bg: '#121827', fg: '#D9DFEE', strong: '#F1F4FB', muted: '#9AA5C2', border: '#2A3553', softBorder: '#212A40',
      panel: '#19202F', hover: '#273350', rowHover: '#1E2740', hoverFg: '#E3E8F5', pressed: '#1E2A47', sel: '#263C6E', selFg: '#F1F4FB', selInactive: '#232D45', selInactiveFg: '#E3E8F5',
      accent: '#6E98FF', accentFg: '#0A1124', accentHover: '#8AACFF', accentPressed: '#5A86F0', focus: '#6E98FF',
      input: '#10162A', inputBorder: '#34416A', inputBorderHover: '#4C5D8E', placeholder: '#9AA5C2',
      disabledFg: '#6F7993', disabledBg: '#161C2C', disabledBorder: '#262F48',
      overlay: '#19202F', overlayBorder: '#2A3553', shadow: '0 10px 28px rgba(0,0,0,.5)', scrim: 'rgba(5,8,16,.6)',
      link: '#8DB4FF', match: '#8DB4FF', matchBg: '#5A4410', matchFg: '#FFF4D6',
      errorFg: '#F48771', warnFg: '#CCA700', okFg: '#73C991', infoFg: '#8DB4FF', error: '#F14C4C', warning: '#CCA700', info: '#3794FF', ok: '#73C991',
      btn: '#1F2840', btnFg: '#E3E8F5', btnBorder: '#34416A', btnHover: '#273350', btnHoverBorder: '#4C5D8E', btnPressed: '#1A2238', btnPressedBorder: '#6E98FF',
      tbHover: '#273350', tbHoverBorder: '#384670', tbPressed: '#1E2A47', tbChecked: '#22345F', tbCheckedBorder: '#6E98FF', tbCheckedFg: '#F1F4FB',
      seg: '#161D2F', segOn: '#6E98FF', segOnFg: '#0A1124',
      chkBorder: '#8A96B6', chkBg: '#10162A', swOff: '#8A96B6',
      toolbar: '#161D2F', toolbarBorder: '#2A3553',
      capActive: '#3A67D6', capActiveFg: '#FFFFFF', capInactive: '#19202F', capInactiveFg: '#D9DFEE',
      tabs: '#1F2840', tabsFg: '#C9D1E6', tabOn: '#19202F', tabOnFg: '#F1F4FB', tabIndicator: '#6E98FF',
      header: '#1C2436', rule: '#1F2840',
      editor: '#141A28', editorFg: '#D9DFEE', lineNo: '#7F8AA8', curLine: '#1A2236',
      chipOk: '#173326', chipOkFg: '#8FDBA8', chipErr: '#3E2025', chipErrFg: '#F8A08F', chipWarn: '#352E13', chipWarnFg: '#E5C24A', chipInfo: '#1B2A4D', chipInfoFg: '#A9C6FF',
      badge: '#273350', badgeFg: '#E3E8F5', dlgFooter: '#161D2C', userBubble: '#21345F', userBubbleFg: '#F1F4FB', codeHeader: '#1C2436',
      titleBar: '#0B101C', titleBarFg: '#E3E8F5',
      icBlue: '#75BEFF', icGreen: '#89D185', icRed: '#F48771', icOrange: '#E8AB53', icPurple: '#B180D7', icYellow: '#FFCC00', icFolder: '#DCB67A', icOutline: '#C5C5C5',
      kw: '#82A9FF', ty: '#6FD1C4', fn: '#F0C97A', st: '#F29E7E', nu: '#C9A2FF', cm: '#84A676', pu: '#D9DFEE'
    },
    'Harbor High contrast': {
      family: 'Harbor', kind: 'hc', radius: 3,
      canvas: '#000000', bg: '#000000', fg: '#FFFFFF', strong: '#FFFFFF', muted: '#D6D6D6', border: '#FFFFFF', softBorder: '#8C8C8C',
      panel: '#000000', hover: '#1A323D', rowHover: '#1A323D', hoverFg: '#FFFFFF', pressed: '#1A323D', sel: '#6CD2FF', selFg: '#000000', selInactive: '#1A323D', selInactiveFg: '#FFFFFF',
      accent: '#6CD2FF', accentFg: '#000000', accentHover: '#AEE6FF', accentPressed: '#6CD2FF', focus: '#6CD2FF',
      input: '#000000', inputBorder: '#FFFFFF', inputBorderHover: '#6CD2FF', placeholder: '#D6D6D6',
      disabledFg: '#9A9A9A', disabledBg: '#000000', disabledBorder: '#6E6E6E',
      overlay: '#000000', overlayBorder: '#FFFFFF', shadow: 'none', scrim: 'rgba(0,0,0,.72)',
      link: '#7CC7FF', match: '#7CC7FF', matchBg: '#000000', matchFg: '#FFE66D',
      errorFg: '#F48771', warnFg: '#FFD700', okFg: '#00FF00', infoFg: '#7CC7FF', error: '#F48771', warning: '#FFD700', info: '#75BEFF', ok: '#00FF00',
      btn: '#000000', btnFg: '#FFFFFF', btnBorder: '#FFFFFF', btnHover: '#1A323D', btnHoverBorder: '#6CD2FF', btnPressed: '#1A323D', btnPressedBorder: '#6CD2FF',
      tbHover: '#1A323D', tbHoverBorder: '#6CD2FF', tbPressed: '#1A323D', tbChecked: '#6CD2FF', tbCheckedBorder: '#6CD2FF', tbCheckedFg: '#000000',
      seg: '#000000', segOn: '#6CD2FF', segOnFg: '#000000',
      chkBorder: '#FFFFFF', chkBg: '#000000', swOff: '#FFFFFF',
      toolbar: '#000000', toolbarBorder: '#FFFFFF',
      capActive: '#6CD2FF', capActiveFg: '#000000', capInactive: '#000000', capInactiveFg: '#FFFFFF',
      tabs: '#000000', tabsFg: '#FFFFFF', tabOn: '#000000', tabOnFg: '#FFFFFF', tabIndicator: '#6CD2FF',
      header: '#000000', rule: '#5C5C5C',
      editor: '#000000', editorFg: '#FFFFFF', lineNo: '#D6D6D6', curLine: '#000000',
      chipOk: '#000000', chipOkFg: '#00FF00', chipErr: '#000000', chipErrFg: '#F48771', chipWarn: '#000000', chipWarnFg: '#FFD700', chipInfo: '#000000', chipInfoFg: '#7CC7FF',
      badge: '#000000', badgeFg: '#FFFFFF', dlgFooter: '#000000', userBubble: '#000000', userBubbleFg: '#FFFFFF', codeHeader: '#000000',
      titleBar: '#000000', titleBarFg: '#FFFFFF',
      icBlue: '#FFFFFF', icGreen: '#FFFFFF', icRed: '#FFFFFF', icOrange: '#FFFFFF', icPurple: '#FFFFFF', icYellow: '#FFFFFF', icFolder: '#FFFFFF', icOutline: '#FFFFFF',
      kw: '#7CC7FF', ty: '#4FF0CF', fn: '#FFE66D', st: '#FFA680', nu: '#E8AAFF', cm: '#9FE870', pu: '#FFFFFF'
    }
  };

  // The proposed tokens: palette key → token name. Existing tokens are listed in controls.md, not here.
  const TOKENS = [
    ['btn', 'Shell.Button', 'Default button fill'],
    ['btnFg', 'Shell.Button.Foreground', 'Default button text'],
    ['btnBorder', 'Shell.Button.Border', 'Default button border'],
    ['btnHover', 'Shell.Button.Hover', 'Default button fill under the pointer'],
    ['btnHoverBorder', 'Shell.Button.Hover.Border', 'Its border'],
    ['btnPressed', 'Shell.Button.Pressed', 'Default button fill while pressed'],
    ['btnPressedBorder', 'Shell.Button.Pressed.Border', 'Its border'],
    ['accentHover', 'Shell.Accent.Hover', 'Primary button under the pointer'],
    ['accentPressed', 'Shell.Accent.Pressed', 'Primary button while pressed'],
    ['tbHover', 'Shell.Toolbar.Hover', 'Toolbar and subtle buttons under the pointer'],
    ['tbHoverBorder', 'Shell.Toolbar.Hover.Border', 'Its border'],
    ['tbPressed', 'Shell.Toolbar.Pressed', 'Toolbar and subtle buttons while pressed'],
    ['tbChecked', 'Shell.Toolbar.Checked', 'A checked toolbar toggle'],
    ['tbCheckedBorder', 'Shell.Toolbar.Checked.Border', 'Its border'],
    ['tbCheckedFg', 'Shell.Toolbar.Checked.Foreground', 'Its text'],
    ['inputBorder', 'Shell.Input.Border', 'Text box and combo box border'],
    ['inputBorderHover', 'Shell.Input.Border.Hover', 'Its border under the pointer'],
    ['placeholder', 'Shell.Input.Placeholder', 'Placeholder text (the muted text at full opacity)'],
    ['disabledFg', 'Shell.Disabled.Foreground', 'Text of a disabled control (exempt from AA; about 3:1)'],
    ['disabledBg', 'Shell.Disabled', 'Fill of a disabled button or field'],
    ['disabledBorder', 'Shell.Disabled.Border', 'Its border'],
    ['chkBorder', 'Shell.Check.Border', 'Unchecked check box, radio button and switch outline (3:1)'],
    ['segOn', 'Shell.Segment.Selected', 'The selected segment of a segmented control'],
    ['segOnFg', 'Shell.Segment.Selected.Foreground', 'Its text'],
    ['selInactive', 'Shell.Selection.Inactive', 'Selected row of a list without the focus'],
    ['selInactiveFg', 'Shell.Selection.Inactive.Foreground', 'Its text'],
    ['rowHover', 'Shell.Row.Hover', 'A list or grid row under the pointer (quieter than a control\'s hover, so colored text stays AA)'],
    ['header', 'Shell.Grid.Header', 'Column headers of tables and grouped lists'],
    ['rule', 'Shell.Grid.Rule', 'Row separators'],
    ['matchBg', 'Shell.Match.Background', 'A find match in result text'],
    ['matchFg', 'Shell.Match.Background.Foreground', 'Text on it'],
    ['badge', 'Shell.Badge', 'Count badges (matches per file, headers, unread)'],
    ['badgeFg', 'Shell.Badge.Foreground', 'Their text'],
    ['chipOk', 'Shell.Status.Success', 'Success chip (HTTP 2xx, passed)'],
    ['chipOkFg', 'Shell.Status.Success.Foreground', 'Its text'],
    ['chipErr', 'Shell.Status.Error', 'Error chip (HTTP 4xx/5xx, SOAP fault)'],
    ['chipErrFg', 'Shell.Status.Error.Foreground', 'Its text'],
    ['chipWarn', 'Shell.Status.Warning', 'Warning chip (HTTP 3xx, stale)'],
    ['chipWarnFg', 'Shell.Status.Warning.Foreground', 'Its text'],
    ['chipInfo', 'Shell.Status.Info', 'Neutral information chip (method, kind)'],
    ['chipInfoFg', 'Shell.Status.Info.Foreground', 'Its text'],
    ['dlgFooter', 'Shell.Dialog.Footer', 'The button row of a dialog'],
    ['scrim', 'Shell.Dialog.Scrim', 'The window behind an in-window dialog'],
    ['userBubble', 'Shell.Chat.User', 'Your messages in AI Chat'],
    ['userBubbleFg', 'Shell.Chat.User.Foreground', 'Their text'],
    ['codeHeader', 'Shell.CodeBlock.Header', 'The header row of a code block (language, Copy, Insert)']
  ];

  // Sizes and shapes (the same in every theme unless the family row says otherwise).
  const SHAPES = [
    ['Shell.Size.Control', '28', 'Buttons, text boxes and combo boxes in dialogs, documents and pages'],
    ['Shell.Size.ToolbarButton, Shell.Size.Input', '24 (existing)', 'The same controls in tool windows, toolbars and dense rows'],
    ['Shell.Size.Button.MinWidth', '80', 'Text buttons in a dialog\'s or page\'s button row'],
    ['Shell.Padding.Button', '12,0', 'Inside a text button (10,0 with an icon on its left)'],
    ['Shell.Padding.ButtonCompact', '8,0', 'Inside a 24 px text or icon + text button'],
    ['Shell.Space.ButtonIcon', '6', 'Between a button\'s icon and its text'],
    ['Shell.Control.CornerRadius', 'Classic 2, Harbor 3', 'Buttons, fields, check boxes; a family value (Graphite 3, Daylight 6, Ember 6 to design)'],
    ['Shell.Focus.Thickness, .Offset', '2, 1', 'The focus ring: Shell.Focus, 1 px outside the control'],
    ['Shell.Padding.Dialog', '20,16', 'Around a dialog\'s header and body; its footer is 20,12 on Shell.Dialog.Footer'],
    ['Shell.Size.Dialog.Width', '520', 'An in-window dialog\'s width; it grows with its content up to 720'],
    ['Shell.Size.Segment', '24', 'A segmented control\'s height'],
    ['Shell.Size.Tab', '28', 'A tab strip inside a tool window or document (2 px accent underline)']
  ];

  const ORDER = ['Classic Blue', 'Classic Light', 'Classic Dark', 'Classic High Contrast', 'Harbor Light', 'Harbor Dark', 'Harbor High contrast'];

  function kebab(key) {
    return key.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase());
  }

  function theme(name) {
    const key = THEMES[name] ? name : 'Harbor Light';
    const p = THEMES[key];
    const vars = Object.entries(p)
      .filter(([k]) => k !== 'family' && k !== 'kind')
      .map(([k, v]) => `--${kebab(k)}:${k === 'radius' ? v + 'px' : v}`)
      .join(';');
    return { name: key, p, vars, cls: `cx fam-${p.family.toLowerCase()} kind-${p.kind}`, hc: p.kind === 'hc', dark: p.kind !== 'light' };
  }

  // Fluent-style 16 px icons. Stroke icons inherit currentColor; a few have a second color through --ic2.
  const S = 'fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"';
  const F = 'fill="currentColor" stroke="none"';
  const ICONS = {
    search: `<g ${S}><circle cx="7" cy="7" r="4.5"/><path d="m10.4 10.4 3.6 3.6"/></g>`,
    refresh: `<g ${S}><path d="M13.2 8.2A5.2 5.2 0 1 1 11.6 4.4"/><path d="M12.6 1.9v2.9H9.7"/></g>`,
    play: `<path ${F} d="M4.6 2.9v10.2a.7.7 0 0 0 1.07.6l8-5.1a.7.7 0 0 0 0-1.2l-8-5.1a.7.7 0 0 0-1.07.6z"/>`,
    'play-outline': `<path ${S} d="M4.6 2.9v10.2a.7.7 0 0 0 1.07.6l8-5.1a.7.7 0 0 0 0-1.2l-8-5.1a.7.7 0 0 0-1.07.6z"/>`,
    'run-all': `<g ${F}><path d="M1.6 3.6v8.8a.6.6 0 0 0 .92.5l6.6-4.4a.6.6 0 0 0 0-1L2.52 3.1a.6.6 0 0 0-.92.5z"/><path d="M8.4 4.2v7.6l5.6-3.8z" opacity=".75"/></g>`,
    stop: `<rect ${F} x="3.5" y="3.5" width="9" height="9" rx="1.6"/>`,
    restart: `<g ${S}><path d="M2.8 8.2A5.2 5.2 0 1 0 4.4 4.4"/><path d="M3.4 1.9v2.9h2.9"/><path d="M7 6.3v3.6l2.8-1.8z" fill="currentColor"/></g>`,
    trash: `<g ${S}><path d="M2.8 4.3h10.4M6.3 4.3V2.8h3.4v1.5"/><path d="M4.3 4.3 5 13a1 1 0 0 0 1 .9h4a1 1 0 0 0 1-.9l.7-8.7M6.8 6.8v4.6M9.2 6.8v4.6"/></g>`,
    globe: `<g ${S}><circle cx="8" cy="8" r="5.8"/><ellipse cx="8" cy="8" rx="2.4" ry="5.8"/><path d="M2.4 6.2h11.2M2.4 9.8h11.2"/></g>`,
    terminal: `<g ${S}><rect x="1.6" y="2.6" width="12.8" height="10.8" rx="1.6"/><path d="m4.4 6.2 2 1.8-2 1.8M7.8 10.2h3.6"/></g>`,
    bug: `<g ${S}><path d="M5.2 6.2h5.6v2.9a2.8 2.8 0 0 1-5.6 0z"/><path d="M6 6.2V5a2 2 0 0 1 4 0v1.2M8 6.2v5.6M2.8 7h2.4M10.8 7h2.4M2.6 10.6h2.8M10.6 10.6h2.8M3.4 3.6 5 5M12.6 3.6 11 5"/></g>`,
    'window-remote': `<g ${S}><rect x="1.6" y="2.6" width="12.8" height="10.8" rx="1.6"/><path d="M1.6 5.4h12.8M6.6 11.2l3.4-3.4M7.2 7.8H10v2.8"/></g>`,
    'window-new': `<g ${S}><path d="M8.6 2.6H3.2a1.6 1.6 0 0 0-1.6 1.6v8.2a1.6 1.6 0 0 0 1.6 1.6h8.2a1.6 1.6 0 0 0 1.6-1.6V7.4"/><path d="M10.6 1.8h3.6v3.6M14.2 1.8 9 7"/></g>`,
    add: `<path ${S} stroke-width="1.4" d="M8 3v10M3 8h10"/>`,
    minus: `<path ${S} stroke-width="1.4" d="M3 8h10"/>`,
    edit: `<g ${S}><path d="M10.4 2.6a1.5 1.5 0 0 1 2.1 0l.9.9a1.5 1.5 0 0 1 0 2.1L6 13H3v-3z"/><path d="m9.2 3.8 3 3"/></g>`,
    pin: `<g ${S}><path d="M10 1.8 14.2 6l-1.3.4-2.6 2.6.2 2.8-1.1 1.1L3.1 6.6l1.1-1.1 2.8.2 2.6-2.6z"/><path d="m6.2 9.8-4.4 4.4"/></g>`,
    list: `<g ${S}><path d="M6 4h7.6M6 8h7.6M6 12h7.6"/></g><g ${F}><circle cx="3" cy="4" r=".95"/><circle cx="3" cy="8" r=".95"/><circle cx="3" cy="12" r=".95"/></g>`,
    tree: `<g ${S}><rect x="1.8" y="1.8" width="4.4" height="3.2" rx=".8"/><rect x="8.8" y="6.4" width="5.4" height="3.2" rx=".8"/><rect x="8.8" y="11" width="5.4" height="3.2" rx=".8"/><path d="M4 5v7.6h4.8M4 8h4.8"/></g>`,
    'arrow-up': `<g ${S}><path d="M8 13.2V2.8M3.8 7 8 2.8 12.2 7"/></g>`,
    'arrow-down': `<g ${S}><path d="M8 2.8v10.4M3.8 9 8 13.2 12.2 9"/></g>`,
    'clear-all': `<g ${S}><path d="M2.4 4h9M2.4 8h5.6M2.4 12h4.4"/><path d="m9.8 9.8 3.8 3.8M13.6 9.8l-3.8 3.8"/></g>`,
    filter: `<path ${S} d="M2.4 3.4h11.2L9.2 8.6v4.6l-2.4-1.2V8.6z"/>`,
    'chevron-down': `<path ${S} stroke-width="1.4" d="m4 6 4 4 4-4"/>`,
    'chevron-right': `<path ${S} stroke-width="1.4" d="m6 4 4 4-4 4"/>`,
    'chevron-up': `<path ${S} stroke-width="1.4" d="m4 10 4-4 4 4"/>`,
    close: `<path ${S} d="m4 4 8 8M12 4l-8 8"/>`,
    copy: `<g ${S}><rect x="5.4" y="5.4" width="8.2" height="8.2" rx="1.6"/><path d="M10.6 5.4V3.8a1.4 1.4 0 0 0-1.4-1.4H3.8a1.4 1.4 0 0 0-1.4 1.4v5.4a1.4 1.4 0 0 0 1.4 1.4h1.6"/></g>`,
    insert: `<g ${S}><path d="M10.2 2.4h3M10.2 13.6h3M11.7 2.4v11.2"/><path d="M2 8h6M5.6 5.6 8 8l-2.4 2.4"/></g>`,
    'doc-check': `<g ${S}><path d="M8.4 14H4.2a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1h4.8l3.4 3.4V8"/><path d="M8.8 2v3.6h3.6"/><path d="m9 11.8 1.6 1.6 3.4-3.4"/></g>`,
    send: `<g ${S}><path d="M2.4 2.6 14 8 2.4 13.4l2-5.4z"/><path d="M4.4 8h5"/></g>`,
    sparkle: `<g ${S}><path d="M7 1.8 8.3 5.6 12 7 8.3 8.4 7 12.2 5.7 8.4 2 7l3.7-1.4z"/><path d="m12.4 10.4.6 1.5 1.5.6-1.5.6-.6 1.5-.6-1.5-1.5-.6 1.5-.6z"/></g>`,
    gear: `<g ${S}><path d="M6.9 1.7h2.2l.4 1.8 1.2.7 1.8-.6 1.1 1.9-1.4 1.3v1.4l1.4 1.3-1.1 1.9-1.8-.6-1.2.7-.4 1.8H6.9l-.4-1.8-1.2-.7-1.8.6-1.1-1.9 1.4-1.3V7.3L2.4 6l1.1-1.9 1.8.6 1.2-.7z"/><circle cx="8" cy="8" r="2"/></g>`,
    folder: `<path ${S} d="M1.6 4.2c0-.6.4-1 1-1h3.4l1.6 1.6h6c.6 0 1 .4 1 1v6.4c0 .6-.4 1-1 1H2.6c-.6 0-1-.4-1-1z"/>`,
    'folder-open': `<g ${S}><path d="M2.2 12.6V4.2c0-.6.4-1 1-1h3l1.6 1.6h4.6c.6 0 1 .4 1 1v1.4"/><path d="M2.2 12.6 4 7.6c.1-.4.5-.6.9-.6h9l-2 5.6H2.2z"/></g>`,
    file: `<g ${S}><path d="M4.2 1.8h4.8l3.4 3.4V14H4.2z"/><path d="M8.8 1.8v3.6h3.6"/></g>`,
    'doc-text': `<g ${S}><path d="M4.2 1.8h4.8l3.4 3.4V14H4.2z"/><path d="M8.8 1.8v3.6h3.6M6.2 8.4h4M6.2 11h4"/></g>`,
    save: `<g ${S}><path d="M2.4 3.4c0-.6.4-1 1-1h7.4l2.8 2.8v7.4c0 .6-.4 1-1 1h-9.2c-.6 0-1-.4-1-1z"/><path d="M5 2.4v3h5v-3M4.6 13.6V9.4h6.8v4.2"/></g>`,
    download: `<g ${S}><path d="M8 2.4v8.2M4.6 7.2 8 10.6l3.4-3.4M3 13.6h10"/></g>`,
    export: `<g ${S}><path d="M8 10V2.4M4.6 5.8 8 2.4l3.4 3.4M2.8 9.6v3a1 1 0 0 0 1 1h8.4a1 1 0 0 0 1-1v-3"/></g>`,
    branch: `<g ${S}><circle cx="4.8" cy="3.8" r="1.6"/><circle cx="4.8" cy="12.2" r="1.6"/><circle cx="11.2" cy="5.2" r="1.6"/><path d="M4.8 5.4v5.2M11.2 6.8c0 2.6-6.4 1.8-6.4 3.8"/></g>`,
    worktree: `<g ${S}><circle cx="8" cy="12.4" r="1.6"/><circle cx="3.6" cy="3.6" r="1.6"/><circle cx="12.4" cy="3.6" r="1.6"/><path d="M8 10.8V8.4M3.6 5.2c0 2 4.4 1.6 4.4 3.2M12.4 5.2c0 2-4.4 1.6-4.4 3.2"/></g>`,
    lock: `<g ${S}><rect x="3.2" y="7" width="9.6" height="6.8" rx="1.4"/><path d="M5.4 7V5a2.6 2.6 0 0 1 5.2 0v2M8 9.6v1.6"/></g>`,
    unlock: `<g ${S}><rect x="3.2" y="7" width="9.6" height="6.8" rx="1.4"/><path d="M5.4 7V5a2.6 2.6 0 0 1 5-1M8 9.6v1.6"/></g>`,
    check: `<path ${S} stroke-width="1.5" d="m3.4 8.4 3 3 6.2-6.8"/>`,
    'check-circle': `<g ${S}><circle cx="8" cy="8" r="6"/><path d="m5.4 8.2 1.8 1.8 3.5-3.7"/></g>`,
    warning: `<path fill="var(--warn-fill, currentColor)" stroke="var(--warn-stroke, currentColor)" stroke-linejoin="round" d="M8 1.6 15 14H1z"/><path stroke="var(--warn-mark, #1B1B1B)" stroke-width="1.5" stroke-linecap="round" d="M8 6v3.6M8 11.6v.1"/>`,
    error: `<circle fill="currentColor" cx="8" cy="8" r="6.5"/><path stroke="var(--badge-mark, #FFFFFF)" stroke-width="1.5" stroke-linecap="round" d="m5.6 5.6 4.8 4.8m0-4.8-4.8 4.8"/>`,
    info: `<circle fill="currentColor" cx="8" cy="8" r="6.5"/><path stroke="var(--badge-mark, #FFFFFF)" stroke-width="1.6" stroke-linecap="round" d="M8 7.2v4.2M8 4.8v.1"/>`,
    history: `<g ${S}><path d="M2.6 8a5.4 5.4 0 1 0 1.6-3.8"/><path d="M2.6 2.4v2.8h2.8M8 5v3.2l2.2 1.4"/></g>`,
    'chat-add': `<g ${S}><path d="M2.4 3.4c0-.6.4-1 1-1h9.2c.6 0 1 .4 1 1v6.4c0 .6-.4 1-1 1H6.8l-3 2.6v-2.6h-.4c-.6 0-1-.4-1-1z"/><path d="M8 4.4v4.2M5.9 6.5h4.2"/></g>`,
    keyboard: `<g ${S}><rect x="1.4" y="3.6" width="13.2" height="8.8" rx="1.6"/><path d="M4.2 6.4h.1M6.7 6.4h.1M9.2 6.4h.1M11.7 6.4h.1M5 9.6h6"/></g>`,
    palette: `<g ${S}><path d="M8 1.8a6.2 6.2 0 0 0 0 12.4c1 0 1.4-.7 1.1-1.5-.4-.9.2-1.9 1.2-1.9h1.5a2.4 2.4 0 0 0 2.4-2.4C14.2 4.6 11.4 1.8 8 1.8z"/></g><g ${F}><circle cx="5" cy="6.4" r="1"/><circle cx="8" cy="4.6" r="1"/><circle cx="11" cy="6.4" r="1"/><circle cx="4.8" cy="9.8" r="1"/></g>`,
    window: `<g ${S}><rect x="1.6" y="2.6" width="12.8" height="10.8" rx="1.6"/><path d="M1.6 5.6h12.8"/></g>`,
    solution: `<g ${S}><rect x="1.6" y="2.6" width="12.8" height="10.8" rx="1.6"/><path d="m4.6 6 2.4 2-2.4 2M8.4 10.4h3.2"/></g>`,
    cube: `<g ${S}><path d="M8 1.8 13.6 4.7v6.6L8 14.2l-5.6-2.9V4.7z"/><path d="m2.4 4.7 5.6 2.9 5.6-2.9M8 7.6v6.6"/></g>`,
    beaker: `<g ${S}><path d="M5.8 1.9h4.4M6.4 1.9v4.2l-3.6 6.5a1 1 0 0 0 .9 1.5h8.6a1 1 0 0 0 .9-1.5L9.6 6.1V1.9M4.4 10h7.2"/></g>`,
    bell: `<g ${S}><path d="M4 11V7a4 4 0 0 1 8 0v4l1.2 1.6H2.8z"/><path d="M6.5 14.2h3"/></g>`,
    broom: `<g ${S}><path d="m13.6 2.4-5 5"/><path d="M8.6 7.4c1 .8 1.5 1.8 1.1 2.9L8.4 13.6 2.4 7.6l3.3-1.3c1.1-.4 2.1.1 2.9 1.1zM5.2 10.4 3.6 12"/></g>`,
    more: `<g ${F}><circle cx="3.6" cy="8" r="1.1"/><circle cx="8" cy="8" r="1.1"/><circle cx="12.4" cy="8" r="1.1"/></g>`,
    'link-external': `<g ${S}><path d="M9.4 2.4h4.2v4.2M13.6 2.4 7.8 8.2"/><path d="M12 9.6v3a1 1 0 0 1-1 1H3.4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3"/></g>`,
    braces: `<g ${S}><path d="M5.6 2.4c-1.5 0-2 .7-2 2v1.4c0 1-.5 1.6-1.4 2.2.9.6 1.4 1.2 1.4 2.2v1.4c0 1.3.5 2 2 2M10.4 2.4c1.5 0 2 .7 2 2v1.4c0 1 .5 1.6 1.4 2.2-.9.6-1.4 1.2-1.4 2.2v1.4c0 1.3-.5 2-2 2"/></g>`,
    table: `<g ${S}><rect x="1.8" y="2.6" width="12.4" height="10.8" rx="1.2"/><path d="M1.8 6h12.4M1.8 9.6h12.4M6 6v7.4"/></g>`,
    container: `<g ${S}><rect x="1.4" y="4" width="13.2" height="8.4" rx="1"/><path d="M4.4 6.2v4M6.9 6.2v4M9.4 6.2v4M11.9 6.2v4"/></g>`,
    layers: `<g ${S}><path d="M8 2 14 5 8 8 2 5z"/><path d="m2 8 6 3 6-3M2 11l6 3 6-3"/></g>`,
    breakpoint: `<circle ${F} cx="8" cy="8" r="5.2"/>`,
    'breakpoint-outline': `<circle ${S} stroke-width="1.5" cx="8" cy="8" r="4.8"/>`,
    reset: `<g ${S}><path d="M3.4 2.6v3h3"/><path d="M3.6 5.6A5.2 5.2 0 1 1 2.8 8.4"/></g>`,
    radar: `<g ${S}><circle cx="8" cy="8" r="5.8"/><circle cx="8" cy="8" r="2.8"/><path d="m8 8 4.2-4.2"/></g><circle ${F} cx="8" cy="8" r="1"/>`,
    'cloud-download': `<g ${S}><path d="M4.6 12.2a3 3 0 0 1-.2-6 3.9 3.9 0 0 1 7.6-.8 2.8 2.8 0 0 1 .4 5.6"/><path d="M8 7.6v6M6 11.6l2 2 2-2"/></g>`,
    wrench: `<path ${S} d="M10.4 1.9a3.1 3.1 0 0 0-2.9 4.2L2.4 11.2a1.3 1.3 0 0 0 1.9 1.9l5.1-5.1a3.1 3.1 0 0 0 4.2-2.9L11.8 6.9l-1.9-.8-.8-1.9z"/>`,
    field: `<g ${S}><path d="M8 2.4 13.6 8 8 13.6 2.4 8z"/></g>`,
    code: `<path ${S} d="M5.4 4.4 1.8 8l3.6 3.6M10.6 4.4 14.2 8l-3.6 3.6"/>`,
    wrap: `<g ${S}><path d="M2.4 4h11.2M2.4 8h9.2a2 2 0 0 1 0 4H8.4M10 10.4 8.4 12l1.6 1.6M2.4 12h3"/></g>`,
    'stop-circle': `<circle ${S} cx="8" cy="8" r="6"/><rect ${F} x="5.6" y="5.6" width="4.8" height="4.8" rx=".8"/>`,
    clock: `<g ${S}><circle cx="8" cy="8" r="6"/><path d="M8 4.6V8l2.2 1.4"/></g>`,
    'arrow-sort': `<g ${S}><path d="M5 13V3M2.6 5.4 5 3l2.4 2.4M11 3v10M8.6 10.6 11 13l2.4-2.4"/></g>`,
    'cs-file': `<path d="M3.6 1.6h6l3 3v9.8H3.6z" fill="none" stroke="var(--ic-outline)"/><rect x="0.8" y="8.2" width="9" height="6.6" rx="1" fill="var(--ic-green)"/><text x="5.3" y="13.3" text-anchor="middle" font-size="5.4" font-weight="700" font-family="Inter, sans-serif" fill="var(--badge-mark, #FFFFFF)">C#</text>`,
    'json-file': `<path d="M3.6 1.6h6l3 3v9.8H3.6z" fill="none" stroke="var(--ic-outline)"/><rect x="0.8" y="8.2" width="9" height="6.6" rx="1" fill="var(--ic-yellow)"/><text x="5.3" y="13.3" text-anchor="middle" font-size="5.4" font-weight="700" font-family="Inter, sans-serif" fill="var(--badge-mark, #FFFFFF)">{}</text>`,
    method: `<g ${S}><path d="M8 2.2 13 5v6L8 13.8 3 11V5z" fill="var(--ic2, none)"/><path d="m3 5 5 2.8L13 5M8 7.8v6"/></g>`,
    plug: `<g ${S}><path d="M5.6 1.8v3M10.4 1.8v3M3.6 4.8h8.8v2.4a4.4 4.4 0 0 1-8.8 0zM8 11.6v2.6"/></g>`
  };

  function sprite() {
    const symbols = Object.entries(ICONS).map(([name, body]) => `<symbol id="i-${name}" viewBox="0 0 16 16">${body}</symbol>`).join('');
    return `<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute;width:0;height:0;overflow:hidden" aria-hidden="true">${symbols}</svg>`;
  }

  function inject() {
    if (!document.getElementById('cx-sprite')) {
      const host = document.createElement('div');
      host.id = 'cx-sprite';
      host.innerHTML = sprite();
      document.body.prepend(host);
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', inject);
  else inject();

  // A small C# colorizer for the mockups' code (results, chat, previews): keywords, strings, numbers, comments, methods
  // (an identifier before "("), types (capitalized identifiers). `match` marks a find match in the tokens.
  const KW = new Set(('public private protected internal static readonly sealed partial class interface record struct enum string int ' +
    'double bool var new return async await using namespace void object get set this null true false if else foreach in throw try catch const').split(' '));
  function csharp(src, match) {
    const out = [];
    const re = /(\/\/.*$)|("(?:[^"\\]|\\.)*")|(\b\d+(?:\.\d+)?\b)|([A-Za-z_][A-Za-z0-9_]*)|(\s+)|([^\sA-Za-z0-9_"]+)/g;
    let m;
    while ((m = re.exec(src))) {
      const t = m[0];
      let c = 'pu';
      if (m[1]) c = 'cm';
      else if (m[2]) c = 'st';
      else if (m[3]) c = 'nu';
      else if (m[4]) {
        if (KW.has(t)) c = 'kw';
        else if (/^\s*[(<]/.test(src.slice(re.lastIndex)) && /^[A-Z]/.test(t) && /^\s*\(/.test(src.slice(re.lastIndex))) c = 'fn';
        else if (/^[A-Z]/.test(t)) c = 'ty';
        else if (/^\s*\(/.test(src.slice(re.lastIndex))) c = 'fn';
      }
      out.push({ t, cls: `t-${c}${match && t === match ? ' hl' : ''}` });
    }
    return out;
  }

  window.CodalusControls = { THEMES, TOKENS, SHAPES, ORDER, ICONS, theme, csharp };
})();
