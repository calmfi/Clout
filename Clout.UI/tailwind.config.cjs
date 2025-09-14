/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Clout.UI/**/*.{razor,cshtml,html}',
    './Clout.UI/**/*.cs',
  ],
  theme: {
    extend: {
      colors: {
        brand: {
          DEFAULT: '#2563eb',
          600: '#2563eb',
          700: '#1d4ed8',
        },
      },
    },
  },
  plugins: [],
}

