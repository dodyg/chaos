import reactHooks from "eslint-plugin-react-hooks";

// Lint is about correctness, not style: hook rules catch real bugs.
export default [
  { ignores: ["dist/**", "node_modules/**", "public/**"] },
  {
    files: ["src/**/*.{js,jsx}"],
    languageOptions: { ecmaVersion: "latest", sourceType: "module", parserOptions: { ecmaFeatures: { jsx: true } } },
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
    },
  },
];
