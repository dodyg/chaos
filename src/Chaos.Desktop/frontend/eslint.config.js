import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";

// Lint is about correctness, not style: the rules that catch real bugs (hook dependencies, unused code, floating promises are left to the compiler).
export default tseslint.config(
  { ignores: ["dist/**", "node_modules/**", "scripts/**"] },
  {
    files: ["src/**/*.{ts,tsx}"],
    extends: [tseslint.configs.base],
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
    },
  },
);
