FROM node:22-slim

WORKDIR /app

# Install deps first for better layer caching
COPY package.json package-lock.json* ./
RUN npm ci --omit=dev || npm install --omit=dev

# Copy source
COPY tsconfig.json ./
COPY src ./src
COPY scripts ./scripts

# Use tsx at runtime — no build step needed
RUN npm install tsx@^4.19.2

# Drop privileges. The node:22-slim image ships with a non-root `node` user
# (UID 1000). Hand ownership of /app to it before switching.
RUN chown -R node:node /app

USER node

ENV NODE_ENV=production

CMD ["npx", "tsx", "src/seller.ts"]
