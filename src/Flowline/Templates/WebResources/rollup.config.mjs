import typescript from '@rollup/plugin-typescript'
import eslint from '@rollup/plugin-eslint';
import fs from 'fs';

// settings
const namespacePrefix = ''; // like 'MyCompany.' (don't forget the dot at the end!)
const fileHeader =
`/**
  * This code is generated using Rollup (https://rollupjs.org/) and TypeScript (https://www.typescriptlang.org/).
  * Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
  */`;

const plugins = [eslint(), typescript()];

// kebab-case/snake_case/camelCase filename -> PascalCase global name
const toPascalCase = (name) => name
    .split(/[^a-zA-Z0-9]+/)
    .filter(Boolean)
    .map(word => word[0].toUpperCase() + word.slice(1))
    .join('');

// generate a bundle statement for each TypeScript and JavaScript file in 'src/', but not its subdirectories
export default fs.readdirSync('src/', { withFileTypes: true })
    .filter(e => e.isFile() && (e.name.endsWith('.ts') || e.name.endsWith('.js')))
    .map(file => ({
        input: `src/${file.name}`,
        output: {
            name: `${namespacePrefix}${toPascalCase(file.name.replace(/\.[^/.]+$/, ""))}`,
            dir: 'dist',
            format: 'iife',
            extend: true,
            sourcemap: false,
            banner: fileHeader
        },
        plugins
    }));
