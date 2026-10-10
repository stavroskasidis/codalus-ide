import js from "@eslint/js";

export default [
  js.configs.recommended,
  {
    files: ["src/**/*.js"],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "module",
      globals: { console: "readonly", document: "readonly" },
    },
    rules: {
      "no-unused-vars": "warn",
      eqeqeq: "error",
    },
  },
];
